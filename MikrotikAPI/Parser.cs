using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MikrotikAPI.Parser
{
  internal enum PacketTypeEnum { Data, Done, Trap, Fatal }

  internal class Response
  {
    public PacketTypeEnum Type { get; }
    public string Tag { get; }

    protected Response(PacketTypeEnum type, string tag)
    {
      Type = type;
      Tag = tag;
    }
  }

  internal class ResponsePacket : Response
  {
    public readonly Dictionary<string, string> Attrs;

    public ResponsePacket(PacketTypeEnum type, Dictionary<string, string> attrs, string tag) : base(type, tag)
    {
      Attrs = attrs;
    }

    public override string ToString()
    {
      return $"[{Type} #{Tag}] {string.Join(" ", Attrs.Select(x => $"{x.Key}={x.Value}"))}";
    }
  }

  internal class FatalResponsePacket : Response
  {
    public string Message { get; }

    public FatalResponsePacket(string message, string tag) : base(PacketTypeEnum.Fatal, tag)
    {
      Message = message;
    }

    public override string ToString()
    {
      return $"[{Type} #{Tag}] {Message}";
    }
  }

  internal enum ParserState
  {
    ReadingCode,
    ReadingData,
    ReadingDone,
    ReadingTrap,
    ReadingFatal,
  }

  internal static class Parser
  {
    public static async Task<Response> ReadSingleResponseAsync(Stream stream, CancellationToken token)
    {
      var state = ParserState.ReadingCode;

      var attributes = new Dictionary<string, string>();
      string tag = null;

      while (true) {
        var wordLength = await Utils.ReadLength(stream, token).ConfigureAwait(false);
        var wordBytes = await Utils.ReadAll(stream, wordLength, token).ConfigureAwait(false);
        var word = wordLength == 0 ? null : Encoding.ASCII.GetString(wordBytes);

        switch (state) {
          case ParserState.ReadingCode:
            state = word switch {
                "!done" => ParserState.ReadingDone,
                "!re" => ParserState.ReadingData,
                "!trap" => ParserState.ReadingTrap,
                "!fatal" => ParserState.ReadingFatal,
                _ => throw new MikrotikInvalidResponseException()
            };
            break;

          case ParserState.ReadingData when word != null:
          case ParserState.ReadingTrap when word != null:
          case ParserState.ReadingDone when word != null:
            var parts = word.Split('=', 3);
            if (parts[0] == ".tag")
              tag = parts[1];
            else
              attributes[parts[1]] = parts[2];
            break;

          case ParserState.ReadingData:
            return new ResponsePacket(PacketTypeEnum.Data, attributes, tag);

          case ParserState.ReadingTrap:
            return new ResponsePacket(PacketTypeEnum.Trap, attributes, tag);

          case ParserState.ReadingDone:
            return new ResponsePacket(PacketTypeEnum.Done, attributes, tag);

          case ParserState.ReadingFatal:
            return new FatalResponsePacket(word, tag);

          default:
            throw new ArgumentOutOfRangeException();
        }
      }
    }
  }
}