using System.Net;
using System.Net.Sockets;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed class LoopbackPortReservation : IDisposable
{
    private TcpListener? listener;

    internal LoopbackPortReservation(TcpListener listener)
    {
        this.listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal int Port { get; }

    public void Dispose()
    {
        TcpListener? ownedListener = Interlocked.Exchange(ref listener, null);
        ownedListener?.Stop();
    }
}

internal static class LoopbackPortAllocator
{
    internal static LoopbackPortReservation Reserve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackPortReservation(listener);
    }
}
