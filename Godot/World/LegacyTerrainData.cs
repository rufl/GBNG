using System.IO.Compression;
using System.Text.Json.Nodes;
using GBNG.Domain;

namespace GBNG.World;

public sealed class LegacyTerrainData
{
    public const int EmptyShape = byte.MaxValue;
    public const int BlockSizeX = 200;
    public const int BlockSizeY = 150;
    public const int BlockSizeZ = 200;
    public const float CellWidth = 2.5f;
    public const float CellHeight = 1.5f;
    public const int CellsYStart = -20;

    private readonly byte[] _shapes;
    private readonly ushort[] _styles;
    private readonly byte[] _directions;

    private LegacyTerrainData(int width, int height, int depth)
    {
        Width = width;
        Height = height;
        Depth = depth;
        int count = checked(width * height * depth);
        _shapes = new byte[count];
        Array.Fill(_shapes, (byte)EmptyShape);
        _styles = new ushort[count];
        _directions = new byte[count];
    }

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public int CellCount => _shapes.Length;

    public static LegacyTerrainData CreateEmpty()
        => new(BlockSizeX, BlockSizeY, BlockSizeZ);

    public static LegacyTerrainData? Decode(JsonObject? gridState)
    {
        string encoded = LegacySaveDocument.ReadString(gridState?["v2Data"]);
        if (encoded.Length == 0)
            return null;

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The .voos terrain payload is not valid base64.", exception);
        }

        using var input = new MemoryStream(compressed, writable: false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new BinaryReader(inflater);

        int version = reader.ReadInt32();
        if (version != 0)
            throw new NotSupportedException($"The terrain payload version {version} is not supported.");

        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        int depth = reader.ReadInt32();
        ValidateDimensions(width, height, depth);

        var terrain = new LegacyTerrainData(width, height, depth);
        for (int y = 0; y < height; y++)
        {
            for (int z = 0; z < depth; z++)
            {
                int segmentCount = reader.ReadInt32();
                if (segmentCount < 1 || segmentCount > width)
                    throw new InvalidDataException($"Invalid terrain segment count {segmentCount} in row ({y}, {z}).");

                int x = 0;
                for (int segment = 0; segment < segmentCount; segment++)
                {
                    int value = reader.ReadInt32();
                    int length = value >> 22;
                    int style = (value >> 13) & 0x1ff;
                    int shape = ((value >> 4) & 0xf) - 1;
                    int direction = value & 0x3;
                    if (length <= 0 || length > width - x)
                        throw new InvalidDataException($"Invalid terrain span length {length} in row ({y}, {z}).");
                    if (shape < -1 || shape > 3)
                        throw new InvalidDataException($"Unsupported terrain shape value {shape} in row ({y}, {z}).");

                    if (shape >= 0)
                    {
                        int rowStart = (y * depth + z) * width;
                        int end = rowStart + x + length;
                        Array.Fill(terrain._shapes, (byte)shape, rowStart + x, length);
                        Array.Fill(terrain._styles, (ushort)style, rowStart + x, length);
                        Array.Fill(terrain._directions, (byte)direction, rowStart + x, length);
                        _ = end;
                    }
                    x += length;
                }

                if (x != width)
                    throw new InvalidDataException($"Terrain row ({y}, {z}) decoded to {x} cells; expected {width}.");
            }
        }

        if (inflater.ReadByte() != -1)
            throw new InvalidDataException("The terrain payload contains trailing unparsed data.");

        return terrain;
    }

    public bool TryGetCell(int x, int y, int z, out byte shape, out ushort style, out byte direction)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || (uint)z >= (uint)Depth)
        {
            shape = (byte)EmptyShape;
            style = 0;
            direction = 0;
            return false;
        }

        int index = (y * Depth + z) * Width + x;
        shape = _shapes[index];
        style = _styles[index];
        direction = _directions[index];
        return shape != EmptyShape;
    }

    public bool SetCell(int x, int y, int z, int shape, int style, int direction)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || (uint)z >= (uint)Depth
            || shape < -1 || shape > 3 || style < 0 || style > 511 || (uint)direction > 3)
            return false;

        int index = (y * Depth + z) * Width + x;
        _shapes[index] = shape < 0 ? (byte)EmptyShape : (byte)shape;
        _styles[index] = shape < 0 ? (ushort)0 : (ushort)style;
        _directions[index] = shape < 0 ? (byte)0 : (byte)direction;
        return true;
    }

    public string Encode()
    {
        using var output = new MemoryStream();
        using (var deflater = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(deflater, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write(Depth);

            for (int y = 0; y < Height; y++)
            {
                for (int z = 0; z < Depth; z++)
                {
                    int rowStart = (y * Depth + z) * Width;
                    int segmentCount = 0;
                    for (int x = 0; x < Width;)
                    {
                        int index = rowStart + x;
                        byte shape = _shapes[index];
                        ushort style = _styles[index];
                        byte direction = _directions[index];
                        int length = 1;
                        while (x + length < Width
                            && _shapes[index + length] == shape
                            && _styles[index + length] == style
                            && _directions[index + length] == direction)
                            length++;
                        x += length;
                        segmentCount++;
                    }

                    writer.Write(segmentCount);
                    for (int x = 0; x < Width;)
                    {
                        int index = rowStart + x;
                        byte shape = _shapes[index];
                        ushort style = _styles[index];
                        byte direction = _directions[index];
                        int length = 1;
                        while (x + length < Width
                            && _shapes[index + length] == shape
                            && _styles[index + length] == style
                            && _directions[index + length] == direction)
                            length++;

                        int encodedShape = shape == EmptyShape ? 0 : shape + 1;
                        int encoded = (length << 22) | (style << 13) | (encodedShape << 4) | direction;
                        writer.Write(encoded);
                        x += length;
                    }
                }
            }
        }

        return Convert.ToBase64String(output.ToArray());
    }

    public IEnumerable<TerrainCell> EnumerateCells()
    {
        for (int y = 0; y < Height; y++)
        {
            for (int z = 0; z < Depth; z++)
            {
                int rowStart = (y * Depth + z) * Width;
                for (int x = 0; x < Width; x++)
                {
                    int index = rowStart + x;
                    if (_shapes[index] != EmptyShape)
                        yield return new TerrainCell(x, y, z, _shapes[index], _styles[index], _directions[index]);
                }
            }
        }
    }

    private static void ValidateDimensions(int width, int height, int depth)
    {
        if (width <= 0 || height <= 0 || depth <= 0 || width > 256 || height > 256 || depth > 256)
            throw new InvalidDataException($"Invalid terrain dimensions: {width}x{height}x{depth}.");

        long count = (long)width * height * depth;
        if (count > 10_000_000)
            throw new InvalidDataException($"Terrain payload exceeds the supported cell limit: {count}.");
    }
}

public readonly record struct TerrainCell(int X, int Y, int Z, byte Shape, ushort Style, byte Direction);
