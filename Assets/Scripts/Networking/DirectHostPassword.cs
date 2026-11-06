using System;
using System.Text;

internal static class DirectHostPassword
{
  internal const int MinimumPasswordBytes = 8;
  internal const int MaximumPasswordBytes = 128;

  internal static bool TryEncode(string password, out byte[] encoded)
  {
    if (string.IsNullOrEmpty(password))
    {
      encoded = Array.Empty<byte>();
      return true;
    }
    if (password.Length > MaximumPasswordBytes)
    {
      encoded = Array.Empty<byte>();
      return false;
    }

    byte[] bytes = Encoding.UTF8.GetBytes(password);
    if (bytes.Length < MinimumPasswordBytes || bytes.Length > MaximumPasswordBytes)
    {
      encoded = Array.Empty<byte>();
      return false;
    }
    encoded = bytes;
    return true;
  }

  internal static bool Matches(byte[] expected, byte[] provided)
  {
    expected = expected ?? Array.Empty<byte>();
    provided = provided ?? Array.Empty<byte>();
    if (expected.Length > MaximumPasswordBytes || provided.Length > MaximumPasswordBytes)
      return false;

    int difference = expected.Length ^ provided.Length;
    for (int i = 0; i < MaximumPasswordBytes; i++)
    {
      byte expectedByte = i < expected.Length ? expected[i] : (byte)0;
      byte providedByte = i < provided.Length ? provided[i] : (byte)0;
      difference |= expectedByte ^ providedByte;
    }
    return difference == 0;
  }
}
