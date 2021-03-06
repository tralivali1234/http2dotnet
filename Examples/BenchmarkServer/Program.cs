﻿using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Http2;
using Http2.Hpack;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "echo")
        {
            echoRequest = true;
        }

        var logProvider = NullLoggerProvider.Instance;
        // Create a TCP socket acceptor
        var listener = new TcpListener(IPAddress.Any, 8888);
        listener.Start();
        Task.Run(() => AcceptTask(listener, logProvider)).Wait();
    }

    static bool echoRequest = false;

    static bool AcceptIncomingStream(IStream stream)
    {
        Task.Run(() =>
        {
            if (echoRequest) EchoHandler(stream);
            else DrainHandler(stream);
        });
        return true;
    }

    static byte[] responseBody = Encoding.ASCII.GetBytes("Hello World!");
    static byte[] emptyBody = new byte[0];

    /// <summary>Echoes all request data back into the response</summary>
    static async void EchoHandler(IStream stream)
    {
        try
        {
            // Consume headers
            var headers = await stream.ReadHeadersAsync();

            // Send response headers
            var responseHeaders = new HeaderField[] {
                new HeaderField { Name = ":status", Value = "200" },
                new HeaderField { Name = "nextone", Value = "i am a header value" },
            };
            await stream.WriteHeadersAsync(responseHeaders, false);

            // Write request payload back into response
            await stream.CopyToAsync(stream);
            // Write end of stream
            await stream.WriteAsync(new ArraySegment<byte>(emptyBody), true);
        }
        catch (Exception e)
        {
            Console.WriteLine("Error during handling request: {0}", e.Message);
            stream.Cancel();
        }
    }

    /// <summary>Drops the request data and writes a sucess response</summary>
    static async void DrainHandler(IStream stream)
    {
        try
        {
            // Consume headers
            var headers = await stream.ReadHeadersAsync();

            // Read the request body to the end
            await stream.DrainAsync();

            // Send a response which consists of headers and a payload
            var responseHeaders = new HeaderField[] {
                new HeaderField { Name = ":status", Value = "200" },
                new HeaderField { Name = "nextone", Value = "i am a header value" },
            };
            await stream.WriteHeadersAsync(responseHeaders, false);
            await stream.WriteAsync(new ArraySegment<byte>(responseBody), true);
        }
        catch (Exception e)
        {
            Console.WriteLine("Error during handling request: {0}", e.Message);
            stream.Cancel();
        }
    }

    static async Task AcceptTask(TcpListener listener, ILoggerProvider logProvider)
    {
        var connectionId = 0;

        var config =
            new ConnectionConfigurationBuilder(isServer: true)
            .UseStreamListener(AcceptIncomingStream)
            .UseHuffmanStrategy(HuffmanStrategy.Never)
            .UseBufferPool(Buffers.Pool)
            .Build();

        while (true)
        {
            // Accept TCP sockets
            var clientSocket = await listener.AcceptSocketAsync();
            clientSocket.NoDelay = true;
            // Create HTTP/2 stream abstraction on top of the socket
            var wrappedStreams = clientSocket.CreateStreams();
            // Alternatively on top of a System.IO.Stream
            //var netStream = new NetworkStream(clientSocket, true);
            //var wrappedStreams = netStream.CreateStreams();

            // Build a HTTP connection on top of the stream abstraction
            var http2Con = new Connection(
                config, wrappedStreams.ReadableStream, wrappedStreams.WriteableStream,
                options: new Connection.Options
                {
                    Logger = logProvider.CreateLogger("HTTP2Conn" + connectionId),
                });

            connectionId++;
        }
    }
}

public static class Buffers
{
    public static ArrayPool<byte> Pool = ArrayPool<byte>.Create(64*1024, 200);
}

public static class RequestUtils
{
    public async static Task DrainAsync(this IReadableByteStream stream)
    {
        var buf = Buffers.Pool.Rent(8*1024);
        var bytesRead = 0;

        try
        {
            while (true)
            {
                var res = await stream.ReadAsync(new ArraySegment<byte>(buf));
                if (res.BytesRead != 0)
                {
                    bytesRead += res.BytesRead;
                }

                if (res.EndOfStream)
                {
                    return;
                }
            }
        }
        finally
        {
            Buffers.Pool.Return(buf);
        }
    }

    public async static Task CopyToAsync(
        this IReadableByteStream stream,
        IWriteableByteStream dest)
    {
        var buf = Buffers.Pool.Rent(64*1024);
        var bytesRead = 0;

        try
        {
            while (true)
            {
                var res = await stream.ReadAsync(new ArraySegment<byte>(buf));
                if (res.BytesRead != 0)
                {
                    await dest.WriteAsync(new ArraySegment<byte>(buf, 0, res.BytesRead));
                    bytesRead += res.BytesRead;
                }

                if (res.EndOfStream)
                {
                    return;
                }
            }
        }
        finally
        {
            Buffers.Pool.Return(buf);
        }
    }
}