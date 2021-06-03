using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BililiveRecorder.Flv;
using BililiveRecorder.Flv.Grouping;
using BililiveRecorder.Flv.Parser;
using BililiveRecorder.Flv.Pipeline;
using BililiveRecorder.Flv.Pipeline.Actions;
using BililiveRecorder.Flv.Writer;
using BililiveRecorder.Flv.Xml;
using BililiveRecorder.ToolBox.ProcessingRules;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BililiveRecorder.ToolBox.Commands
{
    public class FixRequest : ICommandRequest<FixResponse>
    {
        public string Input { get; set; } = string.Empty;

        public string OutputBase { get; set; } = string.Empty;
    }

    public class FixResponse
    {
        public string InputPath { get; set; } = string.Empty;

        public string[] OutputPaths { get; set; } = Array.Empty<string>();

        public bool NeedFix { get; set; }
        public bool Unrepairable { get; set; }

        public int OutputFileCount { get; set; }

        public FlvStats? VideoStats { get; set; }
        public FlvStats? AudioStats { get; set; }

        public int IssueTypeOther { get; set; }
        public int IssueTypeUnrepairable { get; set; }
        public int IssueTypeTimestampJump { get; set; }
        public int IssueTypeTimestampOffset { get; set; }
        public int IssueTypeDecodingHeader { get; set; }
        public int IssueTypeRepeatingData { get; set; }
    }

    public class FixHandler : ICommandHandler<FixRequest, FixResponse>
    {
        private static readonly ILogger logger = Log.ForContext<FixHandler>();

        public Task<CommandResponse<FixResponse>> Handle(FixRequest request) => this.Handle(request, default, null);

        public async Task<CommandResponse<FixResponse>> Handle(FixRequest request, CancellationToken cancellationToken, Func<double, Task>? progress)
        {
            FileStream? flvFileStream = null;
            try
            {
                XmlFlvFile.XmlFlvFileMeta? meta = null;

                var memoryStreamProvider = new RecyclableMemoryStreamProvider();
                var comments = new List<ProcessingComment>();
                var context = new FlvProcessingContext();
                var session = new Dictionary<object, object?>();

                // Input
                string? inputPath;
                IFlvTagReader tagReader;
                var xmlMode = false;
                try
                {
                    inputPath = Path.GetFullPath(request.Input);
                    if (inputPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                    {
                        xmlMode = true;
                        tagReader = await Task.Run(() =>
                        {
                            using var stream = new GZipStream(File.Open(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read), CompressionMode.Decompress);
                            var xmlFlvFile = (XmlFlvFile)XmlFlvFile.Serializer.Deserialize(stream);
                            meta = xmlFlvFile.Meta;
                            return new FlvTagListReader(xmlFlvFile.Tags);
                        });
                    }
                    else if (inputPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        xmlMode = true;
                        tagReader = await Task.Run(() =>
                        {
                            using var stream = File.Open(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            var xmlFlvFile = (XmlFlvFile)XmlFlvFile.Serializer.Deserialize(stream);
                            meta = xmlFlvFile.Meta;
                            return new FlvTagListReader(xmlFlvFile.Tags);
                        });
                    }
                    else
                    {
                        flvFileStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        tagReader = new FlvTagPipeReader(PipeReader.Create(flvFileStream), memoryStreamProvider, skipData: false, logger: logger);
                    }
                }
                catch (Exception ex) when (ex is not FlvException)
                {
                    return new CommandResponse<FixResponse>
                    {
                        Status = ResponseStatus.InputIOError,
                        Exception = ex,
                        ErrorMessage = ex.Message
                    };
                }

                // Output
                var outputPaths = new List<string>();
                IFlvTagWriter tagWriter;
                if (xmlMode)
                {
                    tagWriter = new FlvTagListWriter();
                }
                else
                {
                    var targetProvider = new AutoFixFlvWriterTargetProvider(request.OutputBase);
                    targetProvider.BeforeFileOpen += (sender, path) => outputPaths.Add(path);
                    tagWriter = new FlvTagFileWriter(targetProvider, memoryStreamProvider, logger);
                }

                // Pipeline
                using var grouping = new TagGroupReader(tagReader);
                using var writer = new FlvProcessingContextWriter(tagWriter: tagWriter, allowMissingHeader: true, disableKeyframes: false);
                var statsRule = new StatsRule();
                var pipeline = new ProcessingPipelineBuilder(new ServiceCollection().BuildServiceProvider()).Add(statsRule).AddDefault().AddRemoveFillerData().Build();

                // Run
                await Task.Run(async () =>
                {
                    var count = 0;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var group = await grouping.ReadGroupAsync(cancellationToken).ConfigureAwait(false);
                        if (group is null)
                            break;

                        context.Reset(group, session);
                        pipeline(context);

                        if (context.Comments.Count > 0)
                        {
                            comments.AddRange(context.Comments);
                            logger.Debug("修复逻辑输出 {@Comments}", context.Comments);
                        }

                        await writer.WriteAsync(context).ConfigureAwait(false);

                        foreach (var action in context.Actions)
                            if (action is PipelineDataAction dataAction)
                                foreach (var tag in dataAction.Tags)
                                    tag.BinaryData?.Dispose();

                        if (count++ % 10 == 0 && progress is not null && flvFileStream is not null)
                            await progress((double)flvFileStream.Position / flvFileStream.Length);
                    }
                }).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    return new CommandResponse<FixResponse> { Status = ResponseStatus.Cancelled };

                // Post Run
                if (meta is not null)
                    logger.Information("Xml meta: {@Meta}", meta);

                if (xmlMode)
                {
                    await Task.Run(() =>
                    {
                        var w = (FlvTagListWriter)tagWriter;

                        for (var i = 0; i < w.Files.Count; i++)
                        {
                            var path = Path.ChangeExtension(request.OutputBase, $"fix_p{i + 1:D3}.brec.xml");
                            outputPaths.Add(path);
                            using var file = new StreamWriter(File.Create(path));
                            XmlFlvFile.Serializer.Serialize(file, new XmlFlvFile { Tags = w.Files[i] });
                        }

                        if (w.AlternativeHeaders.Count > 0)
                        {
                            var path = Path.ChangeExtension(request.OutputBase, $"headers.txt");
                            using var writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.None));
                            foreach (var tag in w.AlternativeHeaders)
                            {
                                writer.WriteLine();
                                writer.WriteLine(tag.ToString());
                                writer.WriteLine(tag.BinaryDataForSerializationUseOnly);
                            }
                        }
                    });
                }

                if (cancellationToken.IsCancellationRequested)
                    return new CommandResponse<FixResponse> { Status = ResponseStatus.Cancelled };

                // Result
                var response = await Task.Run(() =>
                {
                    var (videoStats, audioStats) = statsRule.GetStats();

                    var countableComments = comments.Where(x => x.T != CommentType.Logging).ToArray();
                    return new FixResponse
                    {
                        InputPath = inputPath,
                        OutputPaths = outputPaths.ToArray(),
                        OutputFileCount = outputPaths.Count,

                        NeedFix = outputPaths.Count != 1 || countableComments.Any(),
                        Unrepairable = countableComments.Any(x => x.T == CommentType.Unrepairable),

                        VideoStats = videoStats,
                        AudioStats = audioStats,

                        IssueTypeOther = countableComments.Count(x => x.T == CommentType.Other),
                        IssueTypeUnrepairable = countableComments.Count(x => x.T == CommentType.Unrepairable),
                        IssueTypeTimestampJump = countableComments.Count(x => x.T == CommentType.TimestampJump),
                        IssueTypeTimestampOffset = countableComments.Count(x => x.T == CommentType.TimestampOffset),
                        IssueTypeDecodingHeader = countableComments.Count(x => x.T == CommentType.DecodingHeader),
                        IssueTypeRepeatingData = countableComments.Count(x => x.T == CommentType.RepeatingData)
                    };
                });

                return new CommandResponse<FixResponse> { Status = ResponseStatus.OK, Result = response };
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CommandResponse<FixResponse> { Status = ResponseStatus.Cancelled };
            }
            catch (NotFlvFileException ex)
            {
                return new CommandResponse<FixResponse>
                {
                    Status = ResponseStatus.NotFlvFile,
                    Exception = ex,
                    ErrorMessage = ex.Message
                };
            }
            catch (UnknownFlvTagTypeException ex)
            {
                return new CommandResponse<FixResponse>
                {
                    Status = ResponseStatus.UnknownFlvTagType,
                    Exception = ex,
                    ErrorMessage = ex.Message
                };
            }
            catch (Exception ex)
            {
                return new CommandResponse<FixResponse>
                {
                    Status = ResponseStatus.Error,
                    Exception = ex,
                    ErrorMessage = ex.Message
                };
            }
            finally
            {
                flvFileStream?.Dispose();
            }
        }

        public void PrintResponse(FixResponse response)
        {
            Console.Write("Input: ");
            Console.WriteLine(response.InputPath);

            Console.WriteLine(response.NeedFix ? "File needs repair" : "File doesn't need repair");

            if (response.Unrepairable)
                Console.WriteLine("File contains error(s) that are unrepairable (yet), please send sample to the author of this program.");

            Console.WriteLine("{0} file(s) written", response.OutputFileCount);

            foreach (var path in response.OutputPaths)
            {
                Console.Write("  ");
                Console.WriteLine(path);
            }

            Console.WriteLine("Types of error:");
            Console.Write("Other: ");
            Console.WriteLine(response.IssueTypeOther);
            Console.Write("Unrepairable: ");
            Console.WriteLine(response.IssueTypeUnrepairable);
            Console.Write("TimestampJump: ");
            Console.WriteLine(response.IssueTypeTimestampJump);
            Console.Write("DecodingHeader: ");
            Console.WriteLine(response.IssueTypeDecodingHeader);
            Console.Write("RepeatingData: ");
            Console.WriteLine(response.IssueTypeRepeatingData);
        }

        private class AutoFixFlvWriterTargetProvider : IFlvWriterTargetProvider
        {
            private readonly string pathTemplate;
            private int fileIndex = 1;

            public event EventHandler<string>? BeforeFileOpen;

            public AutoFixFlvWriterTargetProvider(string pathTemplate)
            {
                this.pathTemplate = pathTemplate;
            }

            public Stream CreateAlternativeHeaderStream()
            {
                var path = Path.ChangeExtension(this.pathTemplate, "header.txt");
                return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            }

            public (Stream stream, object? state) CreateOutputStream()
            {
                var i = this.fileIndex++;
                var path = Path.ChangeExtension(this.pathTemplate, $"fix_p{i:D3}.flv");
                var fileStream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                BeforeFileOpen?.Invoke(this, path);
                return (fileStream, null);
            }
        }
    }
}
