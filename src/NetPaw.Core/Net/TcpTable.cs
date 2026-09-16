using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetPaw.Net;

/// <summary>One outbound TCP connection still waiting for its SYN to be answered.</summary>
public sealed record TcpEndpoint(string Remote, int Port, int Pid, string Local, int LocalPort = 0)
{
    /// <summary>One socket = one key. A browser opens several parallel connects to the same host:port from one PID; they differ by local port only.</summary>
    public string Key => $"{Remote}:{Port}:{Pid}:{LocalPort}";
}

/// <summary>The SYN_SENT rows of the TCP table. Injected so the watcher rules are tested without Windows.</summary>
public interface ITcpTable
{
    IReadOnlyList<TcpEndpoint> SynSent();
}

public sealed class NullTcpTable : ITcpTable
{
    public IReadOnlyList<TcpEndpoint> SynSent() => [];
}

/// <summary>
/// IP Helper <c>GetExtendedTcpTable</c> with owner PIDs: one in-process call, no elevation, no process
/// spawn. Only SYN_SENT rows are materialised (measured: a stuck connect stays there ~21 s).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpHelperTcpTable : ITcpTable
{
    const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5, MIB_TCP_STATE_SYN_SENT = 3, ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid; }

    [DllImport("iphlpapi.dll")]   // DllImport, not LibraryImport: Core builds without unsafe code
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    public IReadOnlyList<TcpEndpoint> SynSent()
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return [];
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc == ERROR_INSUFFICIENT_BUFFER) { Marshal.FreeHGlobal(buf); buf = Marshal.AllocHGlobal(size); rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0); }
            if (rc != 0) return [];
            var count = Marshal.ReadInt32(buf);
            var list = new List<TcpEndpoint>();
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                if (row.State != MIB_TCP_STATE_SYN_SENT) continue;
                list.Add(new TcpEndpoint(new IPAddress(row.RemoteAddr).ToString(), Port(row.RemotePort), (int)row.OwningPid, new IPAddress(row.LocalAddr).ToString(), Port(row.LocalPort)));
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static int Port(uint networkOrder) => (int)(((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF));
}
