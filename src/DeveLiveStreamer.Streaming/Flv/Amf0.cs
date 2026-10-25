using System.Buffers.Binary;
using System.Text;

namespace DeveLiveStreamer.Streaming.Flv;

/// <summary>An AMF0 ECMA array (associative array), as used for onMetaData.</summary>
public sealed class AmfEcmaArray : Dictionary<string, object?>;

/// <summary>Marker for AMF0 "undefined".</summary>
public sealed class AmfUndefined { public static readonly AmfUndefined Value = new(); }

/// <summary>AMF0 values map to double, bool, string, null, AmfUndefined, Dictionary (object), AmfEcmaArray and List (strict array).</summary>
public static class Amf0
{
    public static byte[] Encode(params object?[] values)
    {
        using var output = new MemoryStream();
        foreach (var value in values) Write(output, value);
        return output.ToArray();
    }

    public static void Write(Stream output, object? value)
    {
        switch (value)
        {
            case null: output.WriteByte(0x05); break;
            case AmfUndefined: output.WriteByte(0x06); break;
            case bool flag: output.WriteByte(0x01); output.WriteByte(flag ? (byte)1 : (byte)0); break;
            case string text:
                var bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length > 0xFFFF) { output.WriteByte(0x0C); WriteUInt32(output, (uint)bytes.Length); }
                else { output.WriteByte(0x02); WriteUInt16(output, bytes.Length); }
                output.Write(bytes);
                break;
            case AmfEcmaArray array:
                output.WriteByte(0x08); WriteUInt32(output, (uint)array.Count); WriteProperties(output, array);
                break;
            case IDictionary<string, object?> map: output.WriteByte(0x03); WriteProperties(output, map); break;
            case IEnumerable<object?> list:
                var items = list.ToList();
                output.WriteByte(0x0A); WriteUInt32(output, (uint)items.Count);
                foreach (var item in items) Write(output, item);
                break;
            default:
                output.WriteByte(0x00);
                Span<byte> number = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(number, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                output.Write(number);
                break;
        }
    }

    public static List<object?> Decode(ReadOnlySpan<byte> data)
    {
        var values = new List<object?>();
        var position = 0;
        while (position < data.Length) values.Add(Read(data, ref position));
        return values;
    }

    private static object? Read(ReadOnlySpan<byte> data, ref int position)
    {
        var marker = data[position++];
        switch (marker)
        {
            case 0x00: { var value = BinaryPrimitives.ReadDoubleBigEndian(data[position..]); position += 8; return value; }
            case 0x01: return data[position++] != 0;
            case 0x02: return ReadString(data, ref position, 2);
            case 0x0C: return ReadString(data, ref position, 4);
            case 0x03: { var map = new Dictionary<string, object?>(); ReadProperties(data, ref position, map); return map; }
            case 0x08: { position += 4; var map = new AmfEcmaArray(); ReadProperties(data, ref position, map); return map; }
            case 0x0A:
            {
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]); position += 4;
                var list = new List<object?>(Math.Min(count, 1024));
                for (var i = 0; i < count; i++) list.Add(Read(data, ref position));
                return list;
            }
            case 0x05: return null;
            case 0x06: return AmfUndefined.Value;
            case 0x0B: { var value = BinaryPrimitives.ReadDoubleBigEndian(data[position..]); position += 10; return value; }
            default: throw new InvalidDataException($"Unsupported AMF0 type 0x{marker:X2}.");
        }
    }

    private static void WriteProperties(Stream output, IEnumerable<KeyValuePair<string, object?>> properties)
    {
        foreach (var (key, value) in properties)
        {
            var name = Encoding.UTF8.GetBytes(key);
            WriteUInt16(output, name.Length); output.Write(name); Write(output, value);
        }
        output.Write([0, 0, 9]);
    }

    private static void ReadProperties(ReadOnlySpan<byte> data, ref int position, IDictionary<string, object?> target)
    {
        while (position + 3 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length == 0 && data[position + 2] == 9) { position += 3; return; }
            var key = ReadString(data, ref position, 2);
            target[key] = Read(data, ref position);
        }
        position = data.Length;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int position, int lengthSize)
    {
        var length = lengthSize == 2 ? BinaryPrimitives.ReadUInt16BigEndian(data[position..]) : (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        position += lengthSize;
        var text = Encoding.UTF8.GetString(data.Slice(position, length));
        position += length;
        return text;
    }

    private static void WriteUInt16(Stream output, int value) { output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value); }
    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }
}
