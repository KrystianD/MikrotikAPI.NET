using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MikrotikAPI
{
  internal static class Utils
  {
    public static async Task<byte[]> ReadAll(Stream stream, uint length, CancellationToken token)
    {
      var data = new byte[length];
      var ptr = 0;
      while (ptr < length)
        ptr += await stream.ReadAsync(data, ptr, (int)length - ptr, token);
      return data;
    }

    public static string EncodePassword(string password, string hash)
    {
      var challengeBytes = new byte[hash.Length / 2];
      for (var i = 0; i <= hash.Length - 2; i += 2)
        challengeBytes[i / 2] = byte.Parse(hash.Substring(i, 2), NumberStyles.HexNumber);

      var passwordBytes = Encoding.ASCII.GetBytes(password);

      using var md5 = new MD5CryptoServiceProvider();
      md5.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
      md5.TransformBlock(passwordBytes, 0, passwordBytes.Length, null, 0);
      md5.TransformFinalBlock(challengeBytes, 0, challengeBytes.Length);

      return string.Join("", md5.Hash.Select(x => x.ToString("x2")));
    }

    public static byte[] EncodeLength(int length)
    {
      var tmp = BitConverter.GetBytes(length);

      if (length < 0x80)
        return new[] { (byte)(tmp[0] | 0x00) };
      else if (length < 0x4000)
        return new[] { (byte)(tmp[1] | 0x80), tmp[0] };
      else if (length < 0x200000)
        return new[] { (byte)(tmp[2] | 0xC0), tmp[1], tmp[0] };
      else if (length < 0x10000000)
        return new[] { (byte)(tmp[3] | 0xE0), tmp[2], tmp[1], tmp[0] };
      else
        return new[] { (byte)0xF0, tmp[3], tmp[2], tmp[1], tmp[0] };
    }

    public static async Task<uint> ReadLength(Stream stream, CancellationToken token)
    {
      var buf = new byte[1];

      async Task<byte> ReadByte() => await stream.ReadAsync(buf, 0, 1, token) == 1 ? buf[0] : throw new MikrotikConnectionException();

      var b1 = await ReadByte();
      if (b1 < 0x80)
        return b1;

      var b2 = await ReadByte();
      if (b1 < 0xC0)
        return BitConverter.ToUInt32(new byte[] { b2, b1, 0, 0 }, 0) ^ 0x8000U;

      var b3 = await ReadByte();
      if (b1 < 0xE0)
        return BitConverter.ToUInt32(new byte[] { b3, b2, b1, 0 }, 0) ^ 0xC00000U;

      var b4 = await ReadByte();
      if (b1 < 0xF0)
        return BitConverter.ToUInt32(new[] { b4, b3, b2, b1 }, 0) ^ 0xE0000000U;

      var b5 = await ReadByte();
      if (b1 == 0xF0)
        return BitConverter.ToUInt32(new[] { b5, b4, b3, b2 }, 0);

      throw new MikrotikConnectionException();
    }
  }
}