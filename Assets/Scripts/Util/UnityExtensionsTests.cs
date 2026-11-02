/*
 * Copyright 2019 Google LLC
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using UnityEngine;
using NUnit.Framework;

public class UnityExtensionsTests
{
  [Test]
  public void TestUtf16Serialize()
  {
    byte[] buffer = new byte[100];
    var writer = new VoosBinaryWriter(buffer);

    string sample = "japanese いろはに";
    writer.WriteUtf16(sample);

    var reader = new VoosBinaryReader(buffer);
    string actual = reader.ReadUtf16();

    Assert.AreEqual(sample, actual);
  }

  [Test]
  public void TestBinaryWriterUsesLegacyLittleEndianLayout()
  {
    byte[] buffer = new byte[27];
    var writer = new VoosBinaryWriter(buffer);
    writer.Write(0x12345678);
    writer.Write((ushort)0x9abc);
    writer.Write(true);
    writer.Write(1f);
    writer.Write(new Quaternion(2f, -1f, 0.5f, 0f));

    byte[] bytes = writer.ToArray();
    CollectionAssert.AreEqual(
      new byte[]
      {
        0x78, 0x56, 0x34, 0x12, 0xbc, 0x9a, 0x01,
        0x00, 0x00, 0x80, 0x3f,
        0x00, 0x00, 0x00, 0x40,
        0x00, 0x00, 0x80, 0xbf,
        0x00, 0x00, 0x00, 0x3f,
        0x00, 0x00, 0x00, 0x00
      },
      bytes);
    Assert.AreEqual(27, writer.Position);

    var reader = new VoosBinaryReader(bytes);
    Assert.AreEqual(0x12345678, reader.ReadInt32());
    Assert.AreEqual((ushort)0x9abc, reader.ReadUInt16());
    Assert.AreEqual(1, reader.ReadByte());
    Assert.AreEqual(1f, reader.ReadSingle());
    Quaternion rotation = reader.ReadQuaternion();
    Assert.AreEqual(2f, rotation.x);
    Assert.AreEqual(-1f, rotation.y);
    Assert.AreEqual(0.5f, rotation.z);
    Assert.AreEqual(0f, rotation.w);
  }

  [Test]
  public void TestQuantize()
  {
    Assert.AreEqual(50f, 51f.Quantize(50f));
    Assert.AreEqual(50f, 49f.Quantize(50f));
    Assert.AreEqual(50f, 74f.Quantize(50f));
    Assert.AreEqual(50f, 26f.Quantize(50f));
    Assert.AreEqual(100f, 76f.Quantize(50f));
    Assert.AreEqual(0f, 24f.Quantize(50f));
  }
}
