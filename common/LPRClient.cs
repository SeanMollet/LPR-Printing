﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace common
{
    public interface ILPRClient
    {
        IEnumerable<string> QueryPrinter(LPQJob lpqJob);
        void PrintFile(LPRJob job);
    }

    public class LPRClient : ILPRClient
    {
        private class ConnectInfo
        {
            public TcpClient Client { get; set; }
            public LPRJob Job { get; set; }
            public EventWaitHandle WaitHandle { get; set; }
        }

        private const int LPRPort = 515;
        private static volatile int _jobNumber; // TODO sync

        public IEnumerable<string> QueryPrinter(LPQJob lpqJob)
        {
            using (var client = new TcpClient(lpqJob.Server, LPRPort))
            using (var stream = client.GetStream())
            using (var streamReader = new StreamReader(stream, Encoding.ASCII))
            {
                var code = lpqJob.Verbose ? '\x04' : '\x03';
                stream.WriteASCII($"{code}{lpqJob.Printer} \n");

                while (!streamReader.EndOfStream)
                {
                    yield return streamReader.ReadLine();
                }
            }
        }

        public void PrintFile(LPRJob job)
        {
            using (var client = new TcpClient())
            {
                var connectInfo = new ConnectInfo
                {
                    Client = client,
                    Job = job,
                    WaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset),
                };


                client.BeginConnect(job.Server, LPRPort, OnConnect, connectInfo);
                connectInfo.WaitHandle.WaitOne();
            }
        }

        private static void OnConnect(IAsyncResult result)
        {
            var connectInfo = (ConnectInfo) result.AsyncState;
            try
            {
                connectInfo.Client.EndConnect(result);

                var machineName = string.Join("", Environment.MachineName.Where(c => c > 32 && c < 128));
                var userName = string.Join("", Environment.UserName.Where(c => c > 32 && c < 128));


                _jobNumber = _jobNumber%999 + 1;
                var jobIdentifier = $"{_jobNumber:D3}{machineName}";

                using (var stream = connectInfo.Client.GetStream())
                {
                    stream.WriteASCII($"\x02{connectInfo.Job.Printer}\n");
                    CheckResult(stream);

                    if (connectInfo.Job.SendDataFileFirst)
                    {
                        WriteDataFile(connectInfo.Job, stream, jobIdentifier);
                        WriteControlFile(connectInfo.Job, stream, machineName, userName, jobIdentifier);
                    }
                    else
                    {
                        WriteControlFile(connectInfo.Job, stream, machineName, userName, jobIdentifier);
                        WriteDataFile(connectInfo.Job, stream, jobIdentifier);
                    }
                }
            }
            catch (ObjectDisposedException)
            {

            }
            finally
            {
                connectInfo.WaitHandle.Set();
            }
        }

        private static void WriteControlFile(LPRJob job, NetworkStream stream, string machineName, string userName, string jobIdentifier)
        {
            var controlFile = new StringBuilder();
            controlFile.Append($"H{machineName}\n");
            controlFile.Append($"P{userName}\n");
            controlFile.Append($"{job.FileType}dfA{jobIdentifier}\n");
            controlFile.Append($"UdfA{jobIdentifier}\n");
            controlFile.Append($"N{job.Path}\n");

            if (job.Class != null)
            {
                controlFile.Append($"C{job.Class}\n");
            }
            if (job.JobName != null)
            {
                controlFile.Append($"J{job.JobName}\n");
            }

            stream.WriteASCII($"\x02{controlFile.Length} cfA{jobIdentifier}\n");
            CheckResult(stream);

            stream.WriteASCII(controlFile.ToString());
            stream.WriteByte(0);
            CheckResult(stream);
        }

        private static void WriteDataFile(LPRJob job, NetworkStream stream, string jobIdentifier)
        {
            var fileSize = new FileInfo(job.Path).Length;
            stream.WriteASCII($"\x03{fileSize} dfA{jobIdentifier}\n");
            CheckResult(stream);

            var fileStream = new FileStream(job.Path, FileMode.Open);
            fileStream.CopyTo(stream);
            stream.WriteByte(0);
            CheckResult(stream);
        }

        private static void CheckResult(NetworkStream stream)
        {
            var result = stream.ReadByte();
            if (result != 0)
            {
                throw new ApplicationException($"Unexpected response from server on receive job: {result}");
            }
        }
    }
}