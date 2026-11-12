using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MatHub.Data;

namespace MatHub.Services.Rollout;

/// <summary>
/// A small read-only TFTP server (RFC 1350, with the options blksize, tsize and timeout of RFC 2347-2349) for the first step of the network boot: it hands out
/// the boot loaders and <c>mathub.ipxe</c> (also as <c>autoexec.ipxe</c>). It listens on UDP 4969 in the container (the compose file maps 69 to it: an
/// unprivileged user cannot open port 69) and answers every transfer from that same port, so the replies pass Docker's port mapping (a new port per transfer,
/// as the RFC suggests, would not). Every transfer is held in memory (boot loaders are a few hundred kilobytes); a block is sent again after the timeout,
/// five times at most.
/// </summary>
/// <summary>What the TFTP server does now (the setup page shows it).</summary>
public sealed class TftpServerInfo
{
    public bool Listening { get; set; }

    public int Port { get; set; }

    public string? Problem { get; set; }

    public int Transfers { get; set; }

    public DateTime? LastTransfer { get; set; }

    public string? LastFile { get; set; }
}

public sealed class TftpServer(IServiceScopeFactory scopes, NetbootFiles files, TftpServerInfo info, IConfiguration configuration, ILogger<TftpServer> logger) : BackgroundService
{
    private const ushort OpRead = 1;
    private const ushort OpWrite = 2;
    private const ushort OpData = 3;
    private const ushort OpAck = 4;
    private const ushort OpError = 5;
    private const ushort OpOptionAck = 6;

    private sealed class Transfer
    {
        public required byte[] Data { get; init; }

        public required int BlockSize { get; init; }

        public required TimeSpan Timeout { get; init; }

        /// <summary>The block last sent (0 = the option acknowledgement).</summary>
        public long Block { get; set; }

        public byte[] LastPacket { get; set; } = [];

        public DateTime SentAt { get; set; }

        public int Retries { get; set; }

        public long Blocks => Data.Length / BlockSize + 1;
    }

    private readonly Dictionary<IPEndPoint, Transfer> _transfers = [];

    public int Port => int.TryParse(configuration["Netboot:TftpPort"], out var p) ? p : 4969;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration["Netboot:Tftp"] is "off" or "false")
        {
            logger.LogInformation("The TFTP server is switched off (Netboot:Tftp).");
            info.Problem = "off";
            return;
        }

        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        }
        catch (SocketException ex)
        {
            logger.LogWarning("The TFTP server cannot listen on UDP {Port}: {Message}. The network boot needs another TFTP server then.", Port, ex.Message);
            info.Port = Port;
            info.Problem = ex.Message;
            return;
        }

        logger.LogInformation("The TFTP server listens on UDP {Port}.", Port);
        info.Port = Port;
        info.Listening = true;
        using (udp)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    var packet = await udp.ReceiveAsync(wait.Token);
                    await HandleAsync(udp, packet.RemoteEndPoint, packet.Buffer, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // the half second is over: look for blocks to send again
                }
                catch (SocketException ex)
                {
                    // a client that went away answers with ICMP "port unreachable", which turns up here
                    logger.LogDebug("TFTP: {Message}", ex.Message);
                }

                await ResendAsync(udp);
            }
        }
    }

    private async Task HandleAsync(UdpClient udp, IPEndPoint remote, byte[] packet, CancellationToken ct)
    {
        if (packet.Length < 4)
        {
            return;
        }

        var op = BinaryPrimitives.ReadUInt16BigEndian(packet);
        switch (op)
        {
            case OpRead:
                await StartAsync(udp, remote, packet, ct);
                break;
            case OpWrite:
                await SendErrorAsync(udp, remote, 2, "MatHub only hands out files.");
                break;
            case OpAck:
                await AcknowledgedAsync(udp, remote, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
                break;
            case OpError:
                _transfers.Remove(remote);
                break;
        }
    }

    private async Task StartAsync(UdpClient udp, IPEndPoint remote, byte[] packet, CancellationToken ct)
    {
        var parts = Encoding.ASCII.GetString(packet, 2, packet.Length - 2).Split('\0');
        if (parts.Length < 2)
        {
            return;
        }

        var name = parts[0].Replace('\\', '/').TrimStart('/');
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; i + 1 < parts.Length; i += 2)
        {
            if (parts[i].Length > 0)
            {
                options[parts[i]] = parts[i + 1];
            }
        }

        var data = await ReadAsync(name, ct);
        if (data is null)
        {
            logger.LogInformation("TFTP: {Remote} asked for {File}, which is not there.", remote.Address, name);
            await SendErrorAsync(udp, remote, 1, "File not found.");
            return;
        }

        var blockSize = options.TryGetValue("blksize", out var b) && int.TryParse(b, out var size) ? Math.Clamp(size, 8, 1468) : 512;
        var timeout = options.TryGetValue("timeout", out var t) && int.TryParse(t, out var seconds) ? Math.Clamp(seconds, 1, 30) : 2;
        var transfer = new Transfer { Data = data, BlockSize = blockSize, Timeout = TimeSpan.FromSeconds(timeout) };
        _transfers[remote] = transfer;
        logger.LogInformation("TFTP: {Remote} loads {File} ({Bytes} bytes).", remote.Address, name, data.Length);
        info.Transfers++;
        info.LastTransfer = DateTime.UtcNow;
        info.LastFile = name;

        // the options the client asked for and the server takes: acknowledged first, then the client acknowledges block 0
        var accepted = new List<(string, string)>();
        if (options.ContainsKey("blksize"))
        {
            accepted.Add(("blksize", blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (options.ContainsKey("tsize"))
        {
            accepted.Add(("tsize", data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (options.ContainsKey("timeout"))
        {
            accepted.Add(("timeout", timeout.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (accepted.Count > 0)
        {
            var oack = new List<byte> { 0, (byte)OpOptionAck };
            foreach (var (key, value) in accepted)
            {
                oack.AddRange(Encoding.ASCII.GetBytes(key));
                oack.Add(0);
                oack.AddRange(Encoding.ASCII.GetBytes(value));
                oack.Add(0);
            }

            transfer.Block = 0;
            await SendAsync(udp, remote, transfer, oack.ToArray());
        }
        else
        {
            await SendBlockAsync(udp, remote, transfer, 1);
        }
    }

    private async Task AcknowledgedAsync(UdpClient udp, IPEndPoint remote, ushort acknowledged)
    {
        if (!_transfers.TryGetValue(remote, out var transfer) || (ushort)transfer.Block != acknowledged)
        {
            return;
        }

        if (transfer.Block >= transfer.Blocks)
        {
            _transfers.Remove(remote);
            return;
        }

        await SendBlockAsync(udp, remote, transfer, transfer.Block + 1);
    }

    private static Task SendBlockAsync(UdpClient udp, IPEndPoint remote, Transfer transfer, long block)
    {
        var offset = (int)Math.Min((block - 1) * transfer.BlockSize, transfer.Data.Length);
        var length = Math.Min(transfer.BlockSize, transfer.Data.Length - offset);
        var packet = new byte[4 + length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, OpData);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)block); // the block number rolls over after 65535
        Buffer.BlockCopy(transfer.Data, offset, packet, 4, length);
        transfer.Block = block;
        return SendAsync(udp, remote, transfer, packet);
    }

    private static async Task SendAsync(UdpClient udp, IPEndPoint remote, Transfer transfer, byte[] packet)
    {
        transfer.LastPacket = packet;
        transfer.SentAt = DateTime.UtcNow;
        transfer.Retries = 0;
        await udp.SendAsync(packet, remote);
    }

    private async Task ResendAsync(UdpClient udp)
    {
        var now = DateTime.UtcNow;
        foreach (var (remote, transfer) in _transfers.Where(t => now - t.Value.SentAt > t.Value.Timeout).ToList())
        {
            if (transfer.Retries >= 5)
            {
                logger.LogInformation("TFTP: {Remote} stopped answering; the transfer ends.", remote.Address);
                _transfers.Remove(remote);
                continue;
            }

            transfer.Retries++;
            transfer.SentAt = now;
            try
            {
                await udp.SendAsync(transfer.LastPacket, remote);
            }
            catch (SocketException)
            {
                _transfers.Remove(remote);
            }
        }
    }

    private static Task SendErrorAsync(UdpClient udp, IPEndPoint remote, ushort code, string message)
    {
        var text = Encoding.ASCII.GetBytes(message);
        var packet = new byte[5 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, OpError);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), code);
        Buffer.BlockCopy(text, 0, packet, 4, text.Length);
        return udp.SendAsync(packet, remote).AsTask();
    }

    /// <summary>The bytes of a file: the first script (made from the settings), else a boot loader.</summary>
    private async Task<byte[]?> ReadAsync(string name, CancellationToken ct)
    {
        if (name is "mathub.ipxe" or "autoexec.ipxe")
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return Encoding.UTF8.GetBytes(await BootEndpoints.EntryScriptAsync(db, configuration, ct));
        }

        return files.Loader(name) is { } path ? await File.ReadAllBytesAsync(path, ct) : null;
    }
}
