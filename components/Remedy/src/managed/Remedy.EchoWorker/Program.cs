using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Diagnostics;
using System.Threading;

namespace Remedy.EchoWorker
{
    public class Program
    {
        private const uint REMEDY_WIRE_MAGIC = 0x52454D44; // "REMD"
        private const ushort REMEDY_WIRE_VERSION = 1;
        private const ushort REMEDY_WIRE_HEADER_SIZE = 36;

        private const ushort REMEDY_WIRE_KIND_REQUEST = 1;
        private const ushort REMEDY_WIRE_KIND_COMPLETION = 2;
        private const ushort REMEDY_WIRE_KIND_PING = 3;
        private const ushort REMEDY_WIRE_KIND_PONG = 4;
        private const ushort REMEDY_WIRE_KIND_QUIESCE = 5;
        private const ushort REMEDY_WIRE_KIND_QUIESCE_ACK = 6;

        public static int Main(string[] args)
        {
            string channelNonce = "";
            foreach (var arg in args)
            {
                if (arg.StartsWith("--channel="))
                {
                    channelNonce = arg.Substring(10);
                }
            }

            if (string.IsNullOrEmpty(channelNonce)) return 1;

            using var pipe = new NamedPipeClientStream(".", "remedy-worker-" + channelNonce, PipeDirection.InOut);
            pipe.Connect(3000);

            bool ignoreQuiesce = false;
            byte[] headerBuf = new byte[36];

            try
            {
                while (pipe.IsConnected)
                {
                    ReadExact(pipe, headerBuf, 36);

                    uint magic = BitConverter.ToUInt32(headerBuf, 0);
                    ushort version = BitConverter.ToUInt16(headerBuf, 4);
                    ushort kind = BitConverter.ToUInt16(headerBuf, 6);
                    ushort headerLen = BitConverter.ToUInt16(headerBuf, 8);
                    uint payloadLen = BitConverter.ToUInt32(headerBuf, 12);
                    ulong requestId = BitConverter.ToUInt64(headerBuf, 16);
                    ulong domainHandle = BitConverter.ToUInt64(headerBuf, 24);
                    uint checksum = BitConverter.ToUInt32(headerBuf, 32);

                    if (magic != REMEDY_WIRE_MAGIC || version != REMEDY_WIRE_VERSION || headerLen != 36)
                    {
                        break;
                    }

                    byte[] payload = new byte[payloadLen];
                    if (payloadLen > 0)
                    {
                        ReadExact(pipe, payload, (int)payloadLen);
                        if (ComputeAdler32(payload, (int)payloadLen) != checksum)
                        {
                            break;
                        }
                    }

                    string payloadStr = Encoding.UTF8.GetString(payload);

                    if (kind == REMEDY_WIRE_KIND_PING)
                    {
                        WriteFrame(pipe, REMEDY_WIRE_KIND_PONG, requestId, domainHandle, null);
                    }
                    else if (kind == REMEDY_WIRE_KIND_REQUEST)
                    {
                        string replyStr = "echo_reply";
                        if (payloadStr.Contains("spawn_child"))
                        {
                            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 > NUL")
                            {
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            var childProc = Process.Start(psi);
                            replyStr = $"echo_reply:child_pid={(childProc != null ? childProc.Id : 0)}";
                        }
                        else if (payloadStr.Contains("ignore_quiesce"))
                        {
                            ignoreQuiesce = true;
                        }
                        else if (payloadStr.Contains("late_completion"))
                        {
                            Thread.Sleep(1500);
                        }

                        byte[] replyBytes = Encoding.UTF8.GetBytes(replyStr);
                        WriteFrame(pipe, REMEDY_WIRE_KIND_COMPLETION, requestId, domainHandle, replyBytes);
                    }
                    else if (kind == REMEDY_WIRE_KIND_QUIESCE)
                    {
                        if (!ignoreQuiesce)
                        {
                            WriteFrame(pipe, REMEDY_WIRE_KIND_QUIESCE_ACK, requestId, domainHandle, null);
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Clean exit
            }

            return 0;
        }

        private static uint ComputeAdler32(byte[] data, int length)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < length; ++i)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static void ReadExact(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0) throw new EndOfStreamException("IPC stream closed unexpectedly");
                total += read;
            }
        }

        private static void WriteFrame(Stream stream, ushort kind, ulong requestId, ulong domainHandle, byte[]? payload)
        {
            byte[] header = new byte[36];
            uint payloadLen = (uint)(payload?.Length ?? 0);
            uint checksum = payloadLen > 0 ? ComputeAdler32(payload!, payload!.Length) : 0;

            BitConverter.GetBytes(REMEDY_WIRE_MAGIC).CopyTo(header, 0);
            BitConverter.GetBytes(REMEDY_WIRE_VERSION).CopyTo(header, 4);
            BitConverter.GetBytes(kind).CopyTo(header, 6);
            BitConverter.GetBytes(REMEDY_WIRE_HEADER_SIZE).CopyTo(header, 8);
            BitConverter.GetBytes((ushort)0).CopyTo(header, 10);
            BitConverter.GetBytes(payloadLen).CopyTo(header, 12);
            BitConverter.GetBytes(requestId).CopyTo(header, 16);
            BitConverter.GetBytes(domainHandle).CopyTo(header, 24);
            BitConverter.GetBytes(checksum).CopyTo(header, 32);

            stream.Write(header, 0, 36);
            if (payloadLen > 0 && payload != null)
            {
                stream.Write(payload, 0, payload.Length);
            }
            stream.Flush();
        }
    }
}
