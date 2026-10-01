using System.Buffers.Binary;

namespace NetPaw.Updates;

/// <summary>
/// Minimal read-only OLE compound file (MS-CFB) reader: just enough to hash an MSI the way Authenticode does
/// (<see cref="MsiAuthenticode"/>). Every index is bounds- and cycle-checked; a malformed file throws
/// <see cref="InvalidDataException"/>, never reads outside the buffer.
/// </summary>
public sealed class CompoundFile
{
    public sealed record Entry(int Id, byte[] RawName, string Name, int Type, byte[] Clsid, uint StartSector, ulong Size, IReadOnlyList<int> Children)
    {
        public bool IsStream => Type == 2;
        public bool IsStorage => Type is 1 or 5;
    }

    const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, MaxRegSect = 0xFFFFFFFA;
    static readonly byte[] Magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    readonly byte[] _b;
    readonly int _sectorSize, _miniSectorSize;
    readonly uint _miniCutoff;
    readonly uint[] _fat, _miniFat;
    readonly byte[] _miniStream;
    public IReadOnlyList<Entry> Entries { get; }
    public Entry Root => Entries[0];

    public static bool IsCompoundFile(ReadOnlySpan<byte> b) => b.Length >= 512 && b[..8].SequenceEqual(Magic);

    public CompoundFile(byte[] bytes)
    {
        _b = bytes;
        if (!IsCompoundFile(bytes)) throw new InvalidDataException("not an OLE compound file");
        var sectorShift = U16(0x1E); var miniShift = U16(0x20);
        if (sectorShift is not (9 or 12) || miniShift != 6) throw new InvalidDataException("unsupported sector size");
        _sectorSize = 1 << sectorShift; _miniSectorSize = 1 << miniShift;
        var fatSectors = U32(0x2C); var firstDir = U32(0x30); _miniCutoff = U32(0x38);
        var firstMiniFat = U32(0x3C); var firstDifat = U32(0x44); var difatSectors = U32(0x48);
        var maxSectors = (uint)((bytes.Length - 512) / _sectorSize + 1);
        if (fatSectors > maxSectors || difatSectors > maxSectors) throw new InvalidDataException("bad header counts");

        // DIFAT: 109 entries in the header, then a chain of DIFAT sectors (last slot = next sector).
        var fatList = new List<uint>();
        for (var i = 0; i < 109 && fatList.Count < fatSectors; i++) fatList.Add(U32(0x4C + i * 4));
        var d = firstDifat; var per = _sectorSize / 4 - 1;
        for (var n = 0; n < difatSectors && fatList.Count < fatSectors; n++)
        {
            var off = SectorOffset(d);
            for (var i = 0; i < per && fatList.Count < fatSectors; i++) fatList.Add(U32(off + i * 4));
            d = U32(off + per * 4);
        }
        _fat = new uint[fatList.Count * (_sectorSize / 4)];
        for (var s = 0; s < fatList.Count; s++)
        {
            var off = SectorOffset(fatList[s]);
            for (var i = 0; i < _sectorSize / 4; i++) _fat[s * (_sectorSize / 4) + i] = U32(off + i * 4);
        }

        var dir = ReadChain(firstDir, ulong.MaxValue);
        var raw = new List<(byte[] name, string str, int type, int left, int right, int child, byte[] clsid, uint start, ulong size)>();
        for (var o = 0; o + 128 <= dir.Length; o += 128)
        {
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(o + 64));
            var type = dir[o + 66];
            if (nameLen > 64 || (nameLen & 1) != 0) nameLen = 0;
            var name = dir.AsSpan(o, nameLen).ToArray();
            var str = System.Text.Encoding.Unicode.GetString(name, 0, Math.Max(0, nameLen - 2));
            var size = sectorShift == 9 ? BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 120)) : BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(o + 120));
            raw.Add((name, str, type, (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 68)), (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 72)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 76)), dir.AsSpan(o + 80, 16).ToArray(), BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 116)), size));
        }
        if (raw.Count == 0 || raw[0].type != 5) throw new InvalidDataException("no root entry");

        // children of a storage = the red-black tree under its child pointer (in-order walk, visit-once)
        var seen = new bool[raw.Count];
        List<int> Children(int child)
        {
            var list = new List<int>(); var stack = new Stack<int>(); var cur = child;
            while (cur >= 0 && cur < raw.Count || stack.Count > 0)
            {
                while (cur >= 0 && cur < raw.Count)
                {
                    if (seen[cur]) throw new InvalidDataException("directory cycle");
                    seen[cur] = true; stack.Push(cur); cur = raw[cur].left;
                }
                cur = stack.Pop(); list.Add(cur); cur = raw[cur].right;
            }
            return list;
        }
        var entries = new Entry[raw.Count];
        seen[0] = true;
        var queue = new Queue<int>(); queue.Enqueue(0);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue(); var r = raw[id];
            var kids = r.type is 1 or 5 ? Children(r.child) : [];
            entries[id] = new Entry(id, r.name, r.str, r.type, r.clsid, r.start, r.size, kids);
            foreach (var k in kids) queue.Enqueue(k);
        }
        Entries = entries;

        _miniFat = firstMiniFat is EndOfChain or FreeSect ? [] : ToUInts(ReadChain(firstMiniFat, ulong.MaxValue));
        _miniStream = Root.StartSector is EndOfChain or FreeSect ? [] : ReadChain(Root.StartSector, Root.Size);
    }

    /// <summary>The direct children of a storage entry (entries unreachable from the root are ignored).</summary>
    public IEnumerable<Entry> ChildrenOf(Entry storage) => storage.Children.Select(i => Entries[i]);

    /// <summary>Stream contents; <paramref name="length"/> as recorded (low 32 bits, like Authenticode's hash).</summary>
    public byte[] Read(Entry e, uint length)
    {
        if (!e.IsStream) throw new InvalidDataException("not a stream");
        if (length < _miniCutoff)
        {
            var outBuf = new byte[length]; var sector = e.StartSector; var done = 0; var hops = 0;
            while (done < length)
            {
                if (sector >= _miniFat.Length || ++hops > _miniFat.Length) throw new InvalidDataException("bad mini chain");
                var off = (long)sector * _miniSectorSize;
                var n = (int)Math.Min(_miniSectorSize, length - done);
                if (off + n > _miniStream.Length) throw new InvalidDataException("mini sector outside the mini stream");
                Buffer.BlockCopy(_miniStream, (int)off, outBuf, done, n); done += n; sector = _miniFat[sector];
            }
            return outBuf;
        }
        return ReadChain(e.StartSector, length);
    }

    byte[] ReadChain(uint start, ulong length)
    {
        using var ms = new MemoryStream();
        var sector = start; var hops = 0;
        while (sector != EndOfChain && (ulong)ms.Length < length)
        {
            if (sector >= MaxRegSect || sector >= _fat.Length || ++hops > _fat.Length) throw new InvalidDataException("bad FAT chain");
            var off = SectorOffset(sector);
            var n = (int)Math.Min((ulong)_sectorSize, length - (ulong)ms.Length);
            ms.Write(_b, off, n); sector = _fat[sector];
        }
        if (length != ulong.MaxValue && (ulong)ms.Length < length) throw new InvalidDataException("stream shorter than its size");
        return ms.ToArray();
    }

    int SectorOffset(uint sector)
    {
        var off = ((long)sector + 1) * _sectorSize;
        if (sector >= MaxRegSect || off + _sectorSize > _b.Length) throw new InvalidDataException("sector outside the file");
        return (int)off;
    }

    ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(o));
    uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(o));
    static uint[] ToUInts(byte[] b) { var r = new uint[b.Length / 4]; for (var i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4)); return r; }
}
