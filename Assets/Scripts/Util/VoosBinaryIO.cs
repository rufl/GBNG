using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Little-endian byte writer for Game Builder's persisted and JavaScript-synchronized payloads.
/// Keep primitive encodings aligned with Assets/Scripts/Behaviors/JavaScript/serialization.js.txt.
/// </summary>
public sealed class VoosBinaryWriter
{
  [StructLayout(LayoutKind.Explicit)]
  struct FloatBits
  {
    [FieldOffset(0)] public float floatValue;
    [FieldOffset(0)] public int intValue;
  }

  byte[] buffer;
  int position;

  public VoosBinaryWriter(byte[] buffer)
  {
    if (buffer == null) throw new ArgumentNullException(nameof(buffer));
    this.buffer = buffer;
  }

  public int Position => position;

  /// <summary>The current backing array. It can change if this writer grows.</summary>
  public byte[] Buffer => buffer;

  public void Write(byte value)
  {
    EnsureCapacity(1);
    buffer[position++] = value;
  }

  public void Write(bool value) => Write(value ? (byte)1 : (byte)0);

  public void Write(short value) => Write(unchecked((ushort)value));

  public void Write(ushort value)
  {
    EnsureCapacity(2);
    buffer[position++] = (byte)value;
    buffer[position++] = (byte)(value >> 8);
  }

  public void Write(int value)
  {
    EnsureCapacity(4);
    uint bits = unchecked((uint)value);
    buffer[position++] = (byte)bits;
    buffer[position++] = (byte)(bits >> 8);
    buffer[position++] = (byte)(bits >> 16);
    buffer[position++] = (byte)(bits >> 24);
  }

  public void Write(uint value)
  {
    EnsureCapacity(4);
    buffer[position++] = (byte)value;
    buffer[position++] = (byte)(value >> 8);
    buffer[position++] = (byte)(value >> 16);
    buffer[position++] = (byte)(value >> 24);
  }

  public void Write(float value)
  {
    FloatBits bits = new FloatBits { floatValue = value };
    Write(bits.intValue);
  }

  public void Write(Quaternion value)
  {
    Write(value.x);
    Write(value.y);
    Write(value.z);
    Write(value.w);
  }

  /// <summary>Copies only the bytes written so far.</summary>
  public byte[] ToArray()
  {
    byte[] result = new byte[position];
    Array.Copy(buffer, result, position);
    return result;
  }

  void EnsureCapacity(int additionalBytes)
  {
    long required = (long)position + additionalBytes;
    if (required <= buffer.Length) return;
    if (required > int.MaxValue) throw new InvalidOperationException("Binary payload exceeds the maximum array size.");

    int newLength = Math.Max(buffer.Length, 64);
    while (newLength < required)
    {
      long grown = newLength + Math.Max(newLength / 2, 1);
      newLength = (int)Math.Min(grown, int.MaxValue);
    }
    Array.Resize(ref buffer, newLength);
  }
}

/// <summary>Reads the little-endian primitive layout written by <see cref="VoosBinaryWriter"/>.</summary>
public sealed class VoosBinaryReader
{
  [StructLayout(LayoutKind.Explicit)]
  struct FloatBits
  {
    [FieldOffset(0)] public float floatValue;
    [FieldOffset(0)] public int intValue;
  }

  readonly byte[] buffer;
  int position;

  public VoosBinaryReader(byte[] buffer)
  {
    if (buffer == null) throw new ArgumentNullException(nameof(buffer));
    this.buffer = buffer;
  }

  public int Position => position;
  public int Length => buffer.Length;

  public byte ReadByte()
  {
    if (position >= buffer.Length) throw new IndexOutOfRangeException("Binary reader reached the end of its buffer.");
    return buffer[position++];
  }

  public short ReadInt16() => unchecked((short)ReadUInt16());

  public ushort ReadUInt16()
  {
    uint low = ReadByte();
    uint high = ReadByte();
    return (ushort)(low | (high << 8));
  }

  public int ReadInt32() => unchecked((int)ReadUInt32());

  public uint ReadUInt32()
  {
    uint b0 = ReadByte();
    uint b1 = ReadByte();
    uint b2 = ReadByte();
    uint b3 = ReadByte();
    return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
  }

  public float ReadSingle()
  {
    FloatBits bits = new FloatBits { intValue = ReadInt32() };
    return bits.floatValue;
  }

  public Quaternion ReadQuaternion()
  {
    return new Quaternion(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
  }
}
