using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests
{
    public class SubtitleEncoderTests
    {
        private const int StreamCount = 8;
        private const int CueCount = 500;

        // A Greek line that requires a non-UTF-8 legacy encoding to reproduce the bug. The accented
        // characters (ά, έ, ή, ί, ό, ύ, ώ) share the same code points in windows-1253 and iso-8859-7,
        // so a Greek-vs-Greek charset misdetection still round-trips correctly.
        private const string GreekText = "Καλημέρα κόσμε, αυτό είναι ένας υπότιτλος.";

        static SubtitleEncoderTests()
        {
            // Mirrors Jellyfin.Server startup so legacy code pages (e.g. Greek windows-1253) are available.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        // Enough Greek text to give the charset detector a strong, unambiguous signal.
        private static string BuildGreekSrt()
        {
            var builder = new StringBuilder();
            for (var i = 1; i <= 8; i++)
            {
                builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
                builder.Append("00:00:0").Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append(",000 --> 00:00:0").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(",000\n");
                builder.Append(GreekText).Append('\n');
                builder.Append("Η γρήγορη καφέ αλεπού πηδάει πάνω από το τεμπέλικο σκυλί.\n\n");
            }

            return builder.ToString();
        }

        public static TheoryData<MediaSourceInfo, MediaStream, SubtitleEncoder.SubtitleInfo> GetReadableFile_Valid_TestData()
        {
            var data = new TheoryData<MediaSourceInfo, MediaStream, SubtitleEncoder.SubtitleInfo>
            {
                {
                    new MediaSourceInfo()
                    {
                        Protocol = MediaProtocol.File
                    },
                    new MediaStream()
                    {
                        Path = "/media/sub.ass",
                        IsExternal = true
                    },
                    new SubtitleEncoder.SubtitleInfo()
                    {
                        Path = "/media/sub.ass",
                        Protocol = MediaProtocol.File,
                        Format = "ass",
                        IsExternal = true
                    }
                },
                {
                    new MediaSourceInfo()
                    {
                        Protocol = MediaProtocol.File
                    },
                    new MediaStream()
                    {
                        Path = "/media/sub.ssa",
                        IsExternal = true
                    },
                    new SubtitleEncoder.SubtitleInfo()
                    {
                        Path = "/media/sub.ssa",
                        Protocol = MediaProtocol.File,
                        Format = "ssa",
                        IsExternal = true
                    }
                },
                {
                    new MediaSourceInfo()
                    {
                        Protocol = MediaProtocol.File
                    },
                    new MediaStream()
                    {
                        Path = "/media/sub.srt",
                        IsExternal = true
                    },
                    new SubtitleEncoder.SubtitleInfo()
                    {
                        Path = "/media/sub.srt",
                        Protocol = MediaProtocol.File,
                        Format = "srt",
                        IsExternal = true
                    }
                },
                {
                    new MediaSourceInfo()
                    {
                        Protocol = MediaProtocol.Http
                    },
                    new MediaStream()
                    {
                        Path = "/media/sub.ass",
                        IsExternal = true
                    },
                    new SubtitleEncoder.SubtitleInfo()
                    {
                        Path = "/media/sub.ass",
                        Protocol = MediaProtocol.File,
                        Format = "ass",
                        IsExternal = true
                    }
                }
            };

            return data;
        }

        [Theory]
        [MemberData(nameof(GetReadableFile_Valid_TestData))]
        public async Task GetReadableFile_Valid_Success(MediaSourceInfo mediaSource, MediaStream subtitleStream, SubtitleEncoder.SubtitleInfo subtitleInfo)
        {
            var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
            var subtitleEncoder = fixture.Create<SubtitleEncoder>();
            var result = await subtitleEncoder.GetReadableFile(mediaSource, subtitleStream, CancellationToken.None);
            Assert.Equal(subtitleInfo.Path, result.Path);
            Assert.Equal(subtitleInfo.Protocol, result.Protocol);
            Assert.Equal(subtitleInfo.Format, result.Format);
            Assert.Equal(subtitleInfo.IsExternal, result.IsExternal);
        }

        public static TheoryData<Encoding> GetSubtitleStream_NonUtf8LocalFile_TestData()
        {
            return
            [
                // Greek legacy encodings – the exact scenario reported in issue #17267.
                Encoding.GetEncoding("windows-1253"),
                Encoding.GetEncoding("iso-8859-7"),
                // Wide encoding with a BOM.
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            ];
        }

        [Theory]
        [MemberData(nameof(GetSubtitleStream_NonUtf8LocalFile_TestData))]
        public async Task GetSubtitleStream_NonUtf8LocalFile_ConvertedToUtf8(Encoding sourceEncoding)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var srt = BuildGreekSrt();
            var path = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(path, srt, sourceEncoding, cancellationToken);

                var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
                var subtitleEncoder = fixture.Create<SubtitleEncoder>();

                var fileInfo = new SubtitleEncoder.SubtitleInfo
                {
                    Path = path,
                    Protocol = MediaProtocol.File,
                    Format = "srt",
                    IsExternal = true
                };

                using var stream = await subtitleEncoder.GetSubtitleStream(fileInfo, cancellationToken);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                var text = await reader.ReadToEndAsync(cancellationToken);

                // The Greek text must survive round-trip and contain no replacement characters.
                Assert.Contains(GreekText, text, StringComparison.Ordinal);
                Assert.DoesNotContain('�', text);
                Assert.DoesNotContain('?', text);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ConvertSubtitles_SequentialCalls_AreDeterministic()
        {
            using var encoder = CreateEncoder();
            var sources = GenerateSources();

            var first = ConvertAllSequential(encoder, sources);
            var second = ConvertAllSequential(encoder, sources);

            for (var i = 0; i < StreamCount; i++)
            {
                Assert.Contains($"S{i}C{CueCount - 1}", first[i], StringComparison.Ordinal);
                Assert.Equal(first[i], second[i]);
            }
        }

        [Fact]
        public async Task GetSubtitleStream_Utf8LocalFile_PreservesContent()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var srt = BuildGreekSrt();
            var path = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(path, srt, new UTF8Encoding(false), cancellationToken);

                var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
                var subtitleEncoder = fixture.Create<SubtitleEncoder>();

                var fileInfo = new SubtitleEncoder.SubtitleInfo
                {
                    Path = path,
                    Protocol = MediaProtocol.File,
                    Format = "srt",
                    IsExternal = true
                };

                using var stream = await subtitleEncoder.GetSubtitleStream(fileInfo, cancellationToken);

                // An already-UTF-8 file must be short-circuited and served directly from disk,
                // not read into memory and re-encoded (which would produce a MemoryStream).
                Assert.IsNotType<MemoryStream>(stream);

                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                var text = await reader.ReadToEndAsync(cancellationToken);

                Assert.Contains(GreekText, text, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task ConvertSubtitles_ConcurrentCalls_MatchSequentialBaseline()
        {
            const int Iterations = 10;

            using var encoder = CreateEncoder();
            var sources = GenerateSources();
            var baseline = ConvertAllSequential(encoder, sources);

            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var results = await Task.WhenAll(Enumerable.Range(0, StreamCount)
                    .Select(i => Task.Run(() => Convert(encoder, sources[i], i)))
                    .ToArray());

                for (var i = 0; i < StreamCount; i++)
                {
                    Assert.True(
                        string.Equals(baseline[i], results[i], StringComparison.Ordinal),
                        $"Iteration {iteration}: stream {i} returned corrupted content ({results[i].Length} chars vs {baseline[i].Length} baseline)");
                }
            }
        }

        [Fact]
        public async Task GetSubtitleStream_UnsupportedEncoding_DoesNotThrow()
        {
            // Regression test for issue #2200
            // When the charset detector identifies an encoding not supported by .NET (e.g., ISO-8859-16),
            // GetSubtitleStream should handle it gracefully instead of throwing ArgumentNullException
            var cancellationToken = TestContext.Current.CancellationToken;
            var srt = BuildGreekSrt();
            var path = Path.GetTempFileName();
            try
            {
                // Write as ISO-8859-7 (Greek) to create a non-UTF-8 file
                await File.WriteAllTextAsync(path, srt, Encoding.GetEncoding("iso-8859-7"), cancellationToken);

                var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
                var subtitleEncoder = fixture.Create<SubtitleEncoder>();

                var fileInfo = new SubtitleEncoder.SubtitleInfo
                {
                    Path = path,
                    Protocol = MediaProtocol.File,
                    Format = "srt",
                    IsExternal = true
                };

                // This should not throw even if the detected encoding was unsupported
                using var stream = await subtitleEncoder.GetSubtitleStream(fileInfo, cancellationToken);
                Assert.NotNull(stream);

                // Verify we can read from the stream without error
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync(cancellationToken);
                Assert.NotEmpty(text);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("pgssub")]
        [InlineData("hdmv_pgs_subtitle")]
        public async Task GetHdrPgsSubtitleFilePath_CachesOriginalTimestampsAndSerializesRequests(string codec)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture(codec);
            var token = TestContext.Current.CancellationToken;

            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token)));

            Assert.All(results, path => Assert.Equal(fixture.CachePath, path));
            Assert.True(Path.IsPathFullyQualified(fixture.CachePath));
            Assert.Equal("complete", await File.ReadAllTextAsync(fixture.CachePath, token));
            Assert.Equal("ordinary cache", await File.ReadAllTextAsync(fixture.OrdinaryCachePath, token));
            Assert.Single(await File.ReadAllLinesAsync(fixture.LogPath, token));

            var args = await File.ReadAllLinesAsync(fixture.ArgumentsPath, token);
            Assert.Contains("-copyts", args);
            Assert.Contains("-avoid_negative_ts", args);
            Assert.Contains("disabled", args);
            Assert.Contains("0:2", args);
            Assert.Contains("-c:s", args);
            Assert.Contains("copy", args);
            Assert.Contains("-f", args);
            Assert.Contains("sup", args);
            Assert.DoesNotContain("-start_at_zero", args);
            Assert.EndsWith(".tmp", args[^1], StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));

            await File.AppendAllTextAsync(fixture.Source.Path, " changed", token);
            var refreshed = await fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token);
            Assert.Equal(fixture.CachePath, refreshed);
            Assert.Equal(2, (await File.ReadAllLinesAsync(fixture.LogPath, token)).Length);
        }

        [Theory]
        [InlineData(7, "partial")]
        [InlineData(0, "")]
        public async Task GetHdrPgsSubtitleFilePath_FailedExtractionPreservesPreviousCache(int exitCode, string output)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            var token = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(fixture.CachePath, "previous cache", token);
            fixture.SetScript($"printf '%s' '{output}' > \"$last\"\nexit {exitCode.ToString(CultureInfo.InvariantCulture)}\n");

            await Assert.ThrowsAsync<FfmpegException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            Assert.Equal("previous cache", await File.ReadAllTextAsync(fixture.CachePath, token));
            Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_SourceChangeFailsWithoutReplacingCache()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            var token = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(fixture.CachePath, "previous cache", token);
            fixture.SetScript($"printf changed >> {ShellQuote(fixture.Source.Path)}\nprintf complete > \"$last\"\n");

            await Assert.ThrowsAsync<IOException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            Assert.Equal("previous cache", await File.ReadAllTextAsync(fixture.CachePath, token));
            Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_CancellationRemovesPartialFile()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            using var cancellation = new CancellationTokenSource();
            fixture.SetScript("printf partial > \"$last\"\nexec sleep 30\n");
            var task = fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, cancellation.Token);

            for (var attempt = 0; attempt < 100 && Directory.GetFiles(fixture.DirectoryPath, "*.tmp").Length == 0; attempt++)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.NotEmpty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(File.Exists(fixture.CachePath));
            Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_RejectsUnsupportedSources()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            var token = TestContext.Current.CancellationToken;

            fixture.Stream.IsExternal = true;
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            fixture.Stream.IsExternal = false;
            fixture.Stream.Codec = "subrip";
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            fixture.Stream.Codec = "pgssub";
            fixture.Source.Protocol = MediaProtocol.Http;
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            fixture.Source.Protocol = MediaProtocol.File;
            fixture.Source.MediaStreams = [];
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            fixture.Source.MediaStreams = [fixture.Stream];
            fixture.Source.Path = fixture.DirectoryPath;
            fixture.Source.VideoType = VideoType.Dvd;
            await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            Assert.False(File.Exists(fixture.LogPath));
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_BluRayConcatTracksSegmentsAndManifest()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            var token = TestContext.Current.CancellationToken;
            var disc = Path.Combine(fixture.DirectoryPath, "Blu-ray disc");
            Directory.CreateDirectory(disc);
            fixture.Source.Path = disc;
            fixture.Source.VideoType = VideoType.BluRay;
            var first = Path.Combine(disc, "first'clip.m2ts");
            var second = Path.Combine(disc, "second clip.m2ts");
            await File.WriteAllTextAsync(first, "first", token);
            await File.WriteAllTextAsync(second, "second", token);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConcatPath)!);
            await File.WriteAllTextAsync(fixture.ConcatPath, ConcatEntry(first, "1") + ConcatEntry(second, "2"), token);

            var cache = await fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token);
            Assert.Equal(fixture.CachePath, cache);
            await fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token);
            Assert.Single(await File.ReadAllLinesAsync(fixture.LogPath, token));
            var args = await File.ReadAllLinesAsync(fixture.ArgumentsPath, token);
            Assert.Equal(new[] { "-copyts", "-f", "concat", "-safe", "0", "-i", fixture.ConcatPath }, args.SkipWhile(arg => arg != "-copyts").Take(7));
            fixture.MediaEncoder.Verify(x => x.GenerateConcatConfig(It.IsAny<MediaSourceInfo>(), It.IsAny<string>()), Times.Never);
            fixture.MediaEncoder.Verify(x => x.GetInputPathArgument(It.IsAny<string>(), It.IsAny<MediaSourceInfo>()), Times.Never);

            await File.AppendAllTextAsync(first, " changed", token);
            await fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token);
            Assert.Equal(2, (await File.ReadAllLinesAsync(fixture.LogPath, token)).Length);

            File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(second).AddSeconds(5));
            await fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token);
            Assert.Equal(3, (await File.ReadAllLinesAsync(fixture.LogPath, token)).Length);

            await File.WriteAllTextAsync(fixture.ConcatPath, ConcatEntry(first, "1.5") + ConcatEntry(second, "2"), token);
            await fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token);
            Assert.Equal(4, (await File.ReadAllLinesAsync(fixture.LogPath, token)).Length);

            File.Delete(first);
            await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            Assert.Equal(4, (await File.ReadAllLinesAsync(fixture.LogPath, token)).Length);
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_BluRayCreatesMissingConcat()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            var token = TestContext.Current.CancellationToken;
            var disc = Path.Combine(fixture.DirectoryPath, "disc");
            Directory.CreateDirectory(disc);
            fixture.Source.Path = disc;
            fixture.Source.VideoType = VideoType.BluRay;
            var segment = Path.Combine(disc, "clip.m2ts");
            await File.WriteAllTextAsync(segment, "segment", token);
            fixture.MediaEncoder.Setup(x => x.GenerateConcatConfig(fixture.Source, fixture.ConcatPath)).Callback(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConcatPath)!);
                File.WriteAllText(fixture.ConcatPath, ConcatEntry(segment, "1"));
            });

            var results = await Task.WhenAll(Enumerable.Range(0, 5)
                .Select(_ => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token)));
            Assert.All(results, path => Assert.Equal(fixture.CachePath, path));
            fixture.MediaEncoder.Verify(x => x.GenerateConcatConfig(fixture.Source, fixture.ConcatPath), Times.Once);
            Assert.Single(await File.ReadAllLinesAsync(fixture.LogPath, token));
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_BluRaySegmentChangedDuringExtractionFails()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            var token = TestContext.Current.CancellationToken;
            var disc = Path.Combine(fixture.DirectoryPath, "disc");
            Directory.CreateDirectory(disc);
            fixture.Source.Path = disc;
            fixture.Source.VideoType = VideoType.BluRay;
            var segment = Path.Combine(disc, "clip.m2ts");
            await File.WriteAllTextAsync(segment, "segment", token);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConcatPath)!);
            await File.WriteAllTextAsync(fixture.ConcatPath, ConcatEntry(segment, "1"), token);
            await File.WriteAllTextAsync(fixture.CachePath, "previous cache", token);
            fixture.SetScript($"printf changed >> {ShellQuote(segment)}\nprintf partial > \"$last\"\n");

            await Assert.ThrowsAsync<IOException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            Assert.Equal("previous cache", await File.ReadAllTextAsync(fixture.CachePath, token));
            Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        }

        [Theory]
        [InlineData("file 'relative.m2ts'\nduration 1\n")]
        [InlineData("option safe 0\n")]
        public async Task GetHdrPgsSubtitleFilePath_RejectsInvalidConcat(string manifest)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            var token = TestContext.Current.CancellationToken;
            var disc = Path.Combine(fixture.DirectoryPath, "disc");
            Directory.CreateDirectory(disc);
            fixture.Source.Path = disc;
            fixture.Source.VideoType = VideoType.BluRay;
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConcatPath)!);
            await File.WriteAllTextAsync(fixture.ConcatPath, manifest, token);

            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            Assert.False(File.Exists(fixture.LogPath));
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_RejectsIncompleteConcat()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            using var fixture = new HdrPgsFixture("pgssub");
            var token = TestContext.Current.CancellationToken;
            var disc = Path.Combine(fixture.DirectoryPath, "disc");
            Directory.CreateDirectory(disc);
            fixture.Source.Path = disc;
            fixture.Source.VideoType = VideoType.BluRay;
            var segment = Path.Combine(disc, "clip.m2ts");
            await File.WriteAllTextAsync(segment, "segment", token);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConcatPath)!);
            await File.WriteAllTextAsync(fixture.ConcatPath, $"file {ShellQuote(segment)}\n", token);

            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token));
            Assert.False(File.Exists(fixture.LogPath));
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_BluRayConcatPreservesTwoClipTimeline()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            var ffmpeg = FindProgram("ffmpeg");
            var ffprobe = FindProgram("ffprobe");
            if (ffmpeg is null || ffprobe is null)
            {
                Assert.Skip("FFmpeg and ffprobe are required for the concat integration test.");
                return;
            }

            using var fixture = new HdrPgsFixture("hdmv_pgs_subtitle", ffmpeg);
            var token = TestContext.Current.CancellationToken;
            var disc = Path.Combine(fixture.DirectoryPath, "disc");
            Directory.CreateDirectory(disc);
            fixture.Source.Path = disc;
            fixture.Source.VideoType = VideoType.BluRay;
            fixture.Source.MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0 }, fixture.Stream];
            var inputSup = Path.Combine(fixture.DirectoryPath, "input.sup");
            var pgs = System.Convert.FromHexString(
                "5047000046500000465016000b028001681000010000000050470000465000004650800000"
                + "504700020f5800020f5816000b0280016810000200000000504700020f5800020f58800000");
            await File.WriteAllBytesAsync(inputSup, pgs, token);
            var first = Path.Combine(disc, "first'clip.m2ts");
            var second = Path.Combine(disc, "second clip.m2ts");
            await Task.WhenAll(new[] { first, second }.Select(path => RunProgram(
                ffmpeg,
                token,
                "-y",
                "-v",
                "error",
                "-f",
                "lavfi",
                "-i",
                "color=black:s=640x360:r=24:d=2",
                "-f",
                "sup",
                "-i",
                inputSup,
                "-map",
                "0:v:0",
                "-map",
                "1:s:0",
                "-c:v",
                "mpeg2video",
                "-c:s",
                "copy",
                "-mpegts_m2ts_mode",
                "1",
                "-f",
                "mpegts",
                path)));
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConcatPath)!);
            await File.WriteAllTextAsync(fixture.ConcatPath, ConcatEntry(first, "2") + ConcatEntry(second, "2"), token);

            var sourcePts = await RunProgram(ffprobe, token, "-v", "error", "-f", "concat", "-safe", "0", "-i", fixture.ConcatPath, "-select_streams", "s:0", "-show_frames", "-show_entries", "subtitle=pts_time", "-of", "csv=p=0");
            var cached = await fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token);
            var cachedPts = await RunProgram(ffprobe, token, "-v", "error", "-select_streams", "s:0", "-show_frames", "-show_entries", "subtitle=pts_time", "-of", "csv=p=0", cached);
            var timestamps = sourcePts.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4, timestamps.Length);
            Assert.Equal(sourcePts.Trim(), cachedPts.Trim());
            Assert.True(double.Parse(timestamps[2], CultureInfo.InvariantCulture) > double.Parse(timestamps[0], CultureInfo.InvariantCulture) + 1);
        }

        [Fact]
        public async Task GetHdrPgsSubtitleFilePath_PreservesNonzeroSourceTimestamps()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The extraction process test uses a Unix shell.");
            var ffmpeg = FindProgram("ffmpeg");
            var ffprobe = FindProgram("ffprobe");
            if (ffmpeg is null || ffprobe is null)
            {
                Assert.Skip("FFmpeg and ffprobe are required for the timestamp integration test.");
                return;
            }

            using var fixture = new HdrPgsFixture("hdmv_pgs_subtitle", ffmpeg);
            fixture.Source.MediaStreams = [fixture.Stream];
            var token = TestContext.Current.CancellationToken;
            var originalSup = Path.Combine(fixture.DirectoryPath, "input.sup");
            var pgs = System.Convert.FromHexString(
                "5047000046500000465016000b028001681000010000000050470000465000004650800000"
                + "504700020f5800020f5816000b0280016810000200000000504700020f5800020f58800000");
            await File.WriteAllBytesAsync(originalSup, pgs, token);
            await RunProgram(
                ffmpeg,
                token,
                "-y",
                "-v",
                "error",
                "-copyts",
                "-itsoffset",
                "5",
                "-f",
                "sup",
                "-i",
                originalSup,
                "-map",
                "0:s:0",
                "-c:s",
                "copy",
                "-avoid_negative_ts",
                "disabled",
                "-f",
                "matroska",
                fixture.Source.Path);

            var cached = await fixture.Encoder.GetHdrPgsSubtitleFilePath(fixture.Stream, fixture.Source, token);
            var sourcePts = await RunProgram(ffprobe, token, "-v", "error", "-select_streams", "s:0", "-show_frames", "-show_entries", "subtitle=pts_time", "-of", "csv=p=0", fixture.Source.Path);
            var cachedPts = await RunProgram(ffprobe, token, "-v", "error", "-select_streams", "s:0", "-show_frames", "-show_entries", "subtitle=pts_time", "-of", "csv=p=0", cached);

            Assert.Equal("5.200000\n6.500000", sourcePts.Trim());
            Assert.Equal(sourcePts.Trim(), cachedPts.Trim());
        }

        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private static string ConcatEntry(string path, string duration)
            => $"file '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'\nduration {duration}\n";

        private static string? FindProgram(string name)
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, name))
                .FirstOrDefault(File.Exists);
        }

        private static async Task<string> RunProgram(string executable, CancellationToken cancellationToken, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start test process.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }

        private static SubtitleEncoder CreateEncoder()
        {
            var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
            fixture.Inject<ISubtitleParser>(new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance));
            return fixture.Create<SubtitleEncoder>();
        }

        private static byte[][] GenerateSources()
        {
            return Enumerable.Range(0, StreamCount)
                .Select(i => Encoding.UTF8.GetBytes(GenerateSrt(i, CueCount)))
                .ToArray();
        }

        private static string Convert(SubtitleEncoder encoder, byte[] source, int streamIndex)
        {
            using var input = new MemoryStream(source);
            var info = new SubtitleEncoder.SubtitleInfo { Path = $"track{streamIndex}.srt", Format = "srt" };
            using var output = encoder.ConvertSubtitles(input, info, "vtt", 0, 0, false);
            return Encoding.UTF8.GetString(output.ToArray());
        }

        private static string[] ConvertAllSequential(SubtitleEncoder encoder, byte[][] sources)
        {
            return sources.Select((source, i) => Convert(encoder, source, i)).ToArray();
        }

        private static string GenerateSrt(int streamIndex, int cueCount)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < cueCount; i++)
            {
                var start = TimeSpan.FromSeconds(i * 4);
                var end = start + TimeSpan.FromSeconds(2);
                builder.Append(i + 1).AppendLine()
                    .Append(start.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture))
                    .Append(" --> ")
                    .AppendLine(end.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture))
                    .Append('S').Append(streamIndex).Append('C').Append(i).AppendLine()
                    .AppendLine();
            }

            return builder.ToString();
        }

        private sealed class HdrPgsFixture : IDisposable
        {
            private readonly string _scriptPath;

            public HdrPgsFixture(string codec, string? executable = null)
            {
                DirectoryPath = Path.Combine(Path.GetTempPath(), "jellyfin-hdr-pgs-tests-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(DirectoryPath);
                _scriptPath = Path.Combine(DirectoryPath, "fake-ffmpeg");
                LogPath = Path.Combine(DirectoryPath, "runs.log");
                ArgumentsPath = Path.Combine(DirectoryPath, "args.log");
                CachePath = Path.Combine(DirectoryPath, "7.hdr-pgs.sup");
                OrdinaryCachePath = Path.Combine(DirectoryPath, "7.sup");
                File.WriteAllText(OrdinaryCachePath, "ordinary cache");
                SetScript("printf complete > \"$last\"\n");

                var inputPath = Path.Combine(DirectoryPath, "video file.mkv");
                File.WriteAllText(inputPath, "source");
                Stream = new MediaStream { Type = MediaStreamType.Subtitle, Index = 7, Codec = codec };
                Source = new MediaSourceInfo
                {
                    Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                    Path = inputPath,
                    Protocol = MediaProtocol.File,
                    MediaStreams =
                    [
                        new MediaStream { Type = MediaStreamType.Video, Index = 0 },
                        new MediaStream { Type = MediaStreamType.Audio, Index = 1 },
                        new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, IsExternal = true, Path = "external.sup" },
                        Stream
                    ]
                };

                var fileSystem = new Mock<IFileSystem>();
                fileSystem.Setup(x => x.GetFileInfo(It.IsAny<string>())).Returns<string>(path =>
                {
                    var info = new FileInfo(path);
                    return new FileSystemMetadata
                    {
                        Exists = info.Exists,
                        Length = info.Exists ? info.Length : 0,
                        LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue
                    };
                });
                MediaEncoder = new Mock<IMediaEncoder>();
                MediaEncoder.SetupGet(x => x.EncoderPath).Returns(executable ?? _scriptPath);
                MediaEncoder.Setup(x => x.GetInputPathArgument(inputPath, Source)).Returns('"' + inputPath + '"');
                var paths = new Mock<IPathManager>();
                paths.Setup(x => x.GetSubtitlePath(Source.Id, 7, ".hdr-pgs.sup")).Returns(CachePath);
                var configuration = new Mock<IServerConfigurationManager>();
                configuration.Setup(x => x.GetConfiguration("encoding")).Returns(new EncodingOptions());
                var applicationPaths = new Mock<IApplicationPaths>();
                applicationPaths.SetupGet(x => x.CachePath).Returns(DirectoryPath);
                configuration.SetupGet(x => x.CommonApplicationPaths).Returns(applicationPaths.Object);

                Encoder = new SubtitleEncoder(
                    NullLogger<SubtitleEncoder>.Instance,
                    fileSystem.Object,
                    MediaEncoder.Object,
                    Mock.Of<IHttpClientFactory>(),
                    Mock.Of<IMediaSourceManager>(),
                    Mock.Of<ISubtitleParser>(),
                    paths.Object,
                    configuration.Object);
            }

            public string DirectoryPath { get; }

            public string LogPath { get; }

            public string ArgumentsPath { get; }

            public string CachePath { get; }

            public string OrdinaryCachePath { get; }

            public string ConcatPath => Path.Join(DirectoryPath, "concat", Source.Id + ".concat");

            public MediaStream Stream { get; }

            public MediaSourceInfo Source { get; }

            public SubtitleEncoder Encoder { get; }

            public Mock<IMediaEncoder> MediaEncoder { get; }

            public void SetScript(string body)
            {
                File.WriteAllText(_scriptPath, "#!/bin/sh\n"
                    + $"printf 'run\\n' >> {ShellQuote(LogPath)}\n"
                    + $"printf '%s\\n' \"$@\" > {ShellQuote(ArgumentsPath)}\n"
                    + "for last do :; done\n"
                    + body);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(_scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            public void Dispose()
            {
                Encoder.Dispose();
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
