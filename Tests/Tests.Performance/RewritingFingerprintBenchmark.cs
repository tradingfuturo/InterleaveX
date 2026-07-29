// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Rewriting;

namespace Microsoft.Coyote.Tests.Performance
{
    public enum FingerprintPayload
    {
        RewriterAssembly,
        LargeJsonDump
    }

    /// <summary>
    /// Records cache-hot-path throughput without turning machine timing into a correctness gate.
    /// </summary>
    [MinColumn, MaxColumn, MeanColumn, Q1Column, Q3Column, RankColumn]
    [MarkdownExporter, HtmlExporter, CsvExporter, CsvMeasurementsExporter]
    public class RewritingFingerprintBenchmark
    {
        private const int JsonSize = 32 * 1024 * 1024;

        private string RootDirectory;
        private string PayloadPath;
        private RewritingCacheValidator Validator;

        [Params(FingerprintPayload.RewriterAssembly, FingerprintPayload.LargeJsonDump)]
        public FingerprintPayload Payload { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            this.RootDirectory = Path.Combine(Path.GetTempPath(),
                "coyote-fingerprint-benchmark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.RootDirectory);
            this.PayloadPath = Path.Combine(this.RootDirectory,
                this.Payload is FingerprintPayload.RewriterAssembly ? "rewriter.dll" : "dump.json");

            if (this.Payload is FingerprintPayload.RewriterAssembly)
            {
                File.Copy(typeof(RewritingEngine).Assembly.Location, this.PayloadPath);
            }
            else
            {
                WriteJsonDump(this.PayloadPath);
            }

            this.Validator = new RewritingCacheValidator(
                HostFileSystem.Instance,
                new RewritingCacheExpectation(
                    schemaVersion: 4,
                    rewriterVersion: "benchmark",
                    rewriterModuleId: "benchmark",
                    configurationHash: "benchmark",
                    assembliesDirectory: this.RootDirectory,
                    outputDirectory: this.RootDirectory,
                    inputPaths: new[] { this.PayloadPath },
                    isReplacingAssemblies: true));
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            if (!string.IsNullOrEmpty(this.RootDirectory) && Directory.Exists(this.RootDirectory))
            {
                Directory.Delete(this.RootDirectory, true);
            }
        }

        [Benchmark(Baseline = true)]
        public string Xxh128() =>
            this.Validator.ComputeFileFingerprint(this.PayloadPath);

        [Benchmark]
        public string Sha256()
        {
            using var stream = new FileStream(this.PayloadPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
            using var algorithm = SHA256.Create();
            return ToHexString(algorithm.ComputeHash(stream));
        }

        private static void WriteJsonDump(string path)
        {
            byte[] block = Encoding.UTF8.GetBytes(
                "{\"method\":\"Example\",\"il\":\"ldarg.0 call instance void Example::Run()\"},\n");
            byte[] tail = Encoding.UTF8.GetBytes("{}]");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, FileOptions.SequentialScan);
            stream.WriteByte((byte)'[');
            while (stream.Length + block.Length + tail.Length < JsonSize)
            {
                stream.Write(block, 0, block.Length);
            }

            stream.Write(tail, 0, tail.Length);
        }

        private static string ToHexString(IEnumerable<byte> bytes)
        {
            var builder = new StringBuilder();
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
