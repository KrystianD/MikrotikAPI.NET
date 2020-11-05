using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KDLib;
using MikrotikAPI.Parser;

namespace MikrotikAPI
{
  public class MikrotikAPI
  {
    private class RequestInfo
    {
      public readonly List<Object> Responses = new List<Object>();
      public Dictionary<string, string> ResponseAttributes;
      public readonly TaskCompletionSource<bool> Tcs = new TaskCompletionSource<bool>();
    }

    public class CommandResponse
    {
      public List<Object> Objects;
      public Dictionary<string, string> ResponseAttributes;
    }

    private readonly object _sync = new object();
    private readonly Dictionary<string, RequestInfo> _requests = new Dictionary<string, RequestInfo>();

    private Stream _connection;
    private CancellationTokenSource _cancellationTokenSource;
    private int _currentTag;

    public Task ConnectAsync(string host, int port, bool ssl, string username, string password)
    {
      return ConnectAsync(host, port, ssl, username, password, (sender, certificate, chain, errors) => true);
    }

    public async Task ConnectAsync(string host, int port, bool ssl, string username, string password, RemoteCertificateValidationCallback remoteCertificateValidationCallback)
    {
      if (_connection != null)
        throw new MikrotikAlreadyConnectedException();

      var client = new TcpClient();

      try {
        await client.ConnectAsync(host, port, TimeSpan.FromSeconds(2));

        if (ssl) {
          var sslStream = new SslStream(client.GetStream());

          await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions() {
              TargetHost = "",
              RemoteCertificateValidationCallback = remoteCertificateValidationCallback,
          }, CancellationToken.None);

          _connection = sslStream;
        }
        else {
          _connection = client.GetStream();
        }

        await SendPacketAsync("/login", null, new Dictionary<string, string> {
            ["name"] = username,
            ["password"] = password,
        });

        var resp1 = await Parser.Parser.ReadSingleResponseAsync(_connection, CancellationToken.None).ConfigureAwait(false);
        if (resp1.Type != PacketTypeEnum.Done)
          throw new MikrotikInvalidCredentialsException();

        var responsePacket = (ResponsePacket)resp1;

        if (responsePacket.Attrs.ContainsKey("ret")) {
          await SendPacketAsync("/login", null, new Dictionary<string, string> {
              ["name"] = username,
              ["response"] = $"00{Utils.EncodePassword(password, responsePacket.Attrs["ret"])}",
          });

          var resp2 = await Parser.Parser.ReadSingleResponseAsync(_connection, CancellationToken.None).ConfigureAwait(false);
          if (resp2.Type != PacketTypeEnum.Done)
            throw new MikrotikInvalidCredentialsException();
        }

        var _ = ReaderTask();
        _cancellationTokenSource = new CancellationTokenSource();
      }
      catch {
        if (_connection != null) {
          _connection.Close();
          _connection = null;
        }

        throw;
      }
    }

    public void Close()
    {
      if (_connection == null)
        return;

      DisconnectInternal(new SocketException());
    }

    public async Task<List<Object>> ExecuteCommandAsync(string command, Dictionary<string, string> attributes = null)
    {
      var resp = await ExecuteCommandAsyncEx(command, attributes);
      return resp.Objects;
    }

    public async Task<CommandResponse> ExecuteCommandAsyncEx(string command, Dictionary<string, string> attributes = null)
    {
      var reqInfo = new RequestInfo();
      string tagStr;

      lock (_sync) {
        _currentTag++;

        tagStr = _currentTag.ToString();
        _requests[tagStr] = reqInfo;
      }

      try {
        await SendPacketAsync(command, tagStr, attributes).ConfigureAwait(false);

        if (!await WaitFutureTimeout(reqInfo.Tcs.Task, TimeSpan.FromSeconds(20)).ConfigureAwait(false))
          throw new TimeoutException();

        return new CommandResponse() {
            Objects = reqInfo.Responses,
            ResponseAttributes = reqInfo.ResponseAttributes,
        };
      }
      catch (MikrotikTrapException) {
        throw;
      }
      catch (TimeoutException e) {
        DisconnectInternal(e);
        throw;
      }
      catch {
        DisconnectInternal(new MikrotikInternalException());
        throw;
      }
      finally {
        lock (_sync) {
          if (_requests.ContainsKey(tagStr))
            _requests.Remove(tagStr);
        }
      }
    }

    private async Task ReaderTask()
    {
      try {
        for (;;) {
          var resp = await Parser.Parser.ReadSingleResponseAsync(_connection, CancellationToken.None).ConfigureAwait(false);

          if (resp.Type == PacketTypeEnum.Fatal) {
            DisconnectInternal(new MikrotikFatalException(((FatalResponsePacket)resp).Message));
            return;
          }

          lock (_sync) {
            if (_requests.TryGetValue(resp.Tag, out var reqInfo)) {
              switch (resp.Type) {
                case PacketTypeEnum.Data:
                  reqInfo.Responses.Add(new Object(((ResponsePacket)resp).Attrs));
                  break;
                case PacketTypeEnum.Done:
                  reqInfo.ResponseAttributes = ((ResponsePacket)resp).Attrs;
                  _requests.Remove(resp.Tag);
                  reqInfo.Tcs.SetResult(true);
                  break;
                case PacketTypeEnum.Trap:
                  var respPacket = (ResponsePacket)resp;
                  _requests.Remove(resp.Tag);
                  reqInfo.Tcs.SetException(new MikrotikTrapException(string.Join(" ", respPacket.Attrs.Select(x => $"{x.Key}={x.Value}"))));
                  break;
                default:
                  throw new ArgumentOutOfRangeException();
              }
            }
          }
        }
      }
      catch (Exception) {
        DisconnectInternal(new MikrotikInternalException());
      }
    }

    private void DisconnectInternal(Exception e)
    {
      lock (_sync) {
        foreach (var request in _requests.Values)
          request.Tcs.TrySetException(e);

        _requests.Clear();
        _cancellationTokenSource?.Cancel();
        if (_connection != null) {
          _connection.Close();
          _connection = null;
        }
      }
    }

    private static void AppendAPIWord(MemoryStream ms, string data)
    {
      var payloadBytes = Encoding.ASCII.GetBytes(data);
      var lengthBytes = Utils.EncodeLength(payloadBytes.Length);

      ms.Write(lengthBytes, 0, lengthBytes.Length);
      ms.Write(payloadBytes, 0, payloadBytes.Length);
    }

    private static byte[] BuildAPIPacket(string command, string tag = null, Dictionary<string, string> attributes = null)
    {
      var ms = new MemoryStream();
      AppendAPIWord(ms, command);

      if (tag != null)
        AppendAPIWord(ms, $".tag={tag}");

      if (attributes != null) {
        foreach (var (name, value) in attributes) {
          var nameStr = name.StartsWith("?") ? name : "=" + name;
          AppendAPIWord(ms, $"{nameStr}={value}");
        }
      }

      ms.WriteByte(0);

      return ms.ToArray();
    }

    private async Task SendPacketAsync(string command, string tag = null, Dictionary<string, string> attributes = null)
    {
      if (_connection == null)
        throw new MikrotikNotConnectedException();

      await _connection.WriteAsync(BuildAPIPacket(command, tag, attributes)).ConfigureAwait(false);
    }

    private static async Task<bool> WaitFutureTimeout(Task task, TimeSpan timeout)
    {
      using (var timeoutCancellationTokenSource = new CancellationTokenSource()) {
        var completed = await Task.WhenAny(task, Task.Delay(timeout, timeoutCancellationTokenSource.Token));
        if (completed == task) {
          timeoutCancellationTokenSource.Cancel();
          if (completed.IsFaulted && completed.Exception != null)
            if (completed.Exception.InnerException != null)
              throw completed.Exception.InnerException;
            else
              throw completed.Exception;
          return true;
        }
        else {
          return false;
        }
      }
    }
  }
}