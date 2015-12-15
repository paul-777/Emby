﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.IO;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommonIO;

namespace MediaBrowser.MediaEncoding.Encoder
{
    public abstract class BaseEncoder
    {
        protected readonly MediaEncoder MediaEncoder;
        protected readonly ILogger Logger;
        protected readonly IServerConfigurationManager ConfigurationManager;
        protected readonly IFileSystem FileSystem;
        protected readonly IIsoManager IsoManager;
        protected readonly ILibraryManager LibraryManager;
        protected readonly ISessionManager SessionManager;
        protected readonly ISubtitleEncoder SubtitleEncoder;
        protected readonly IMediaSourceManager MediaSourceManager;

        protected readonly CultureInfo UsCulture = new CultureInfo("en-US");

        protected BaseEncoder(MediaEncoder mediaEncoder,
            ILogger logger,
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            IIsoManager isoManager,
            ILibraryManager libraryManager,
            ISessionManager sessionManager, 
            ISubtitleEncoder subtitleEncoder, 
            IMediaSourceManager mediaSourceManager)
        {
            MediaEncoder = mediaEncoder;
            Logger = logger;
            ConfigurationManager = configurationManager;
            FileSystem = fileSystem;
            IsoManager = isoManager;
            LibraryManager = libraryManager;
            SessionManager = sessionManager;
            SubtitleEncoder = subtitleEncoder;
            MediaSourceManager = mediaSourceManager;
        }

        public async Task<EncodingJob> Start(EncodingJobOptions options,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var encodingJob = await new EncodingJobFactory(Logger, LibraryManager, MediaSourceManager, ConfigurationManager)
                .CreateJob(options, IsVideoEncoder, progress, cancellationToken).ConfigureAwait(false);

            encodingJob.OutputFilePath = GetOutputFilePath(encodingJob);
            FileSystem.CreateDirectory(Path.GetDirectoryName(encodingJob.OutputFilePath));

            encodingJob.ReadInputAtNativeFramerate = options.ReadInputAtNativeFramerate;

            await AcquireResources(encodingJob, cancellationToken).ConfigureAwait(false);

            var commandLineArgs = GetCommandLineArguments(encodingJob);

            if (GetEncodingOptions().EnableDebugLogging)
            {
                commandLineArgs = "-loglevel debug " + commandLineArgs;
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,

                    // Must consume both stdout and stderr or deadlocks may occur
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,

                    FileName = MediaEncoder.EncoderPath,
                    Arguments = commandLineArgs,

                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                },

                EnableRaisingEvents = true
            };

            var workingDirectory = GetWorkingDirectory(options);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                process.StartInfo.WorkingDirectory = workingDirectory;
            }

            OnTranscodeBeginning(encodingJob);

            var commandLineLogMessage = process.StartInfo.FileName + " " + process.StartInfo.Arguments;
            Logger.Info(commandLineLogMessage);

            var logFilePath = Path.Combine(ConfigurationManager.CommonApplicationPaths.LogDirectoryPath, "transcode-" + Guid.NewGuid() + ".txt");
            FileSystem.CreateDirectory(Path.GetDirectoryName(logFilePath));

            // FFMpeg writes debug/error info to stderr. This is useful when debugging so let's put it in the log directory.
            encodingJob.LogFileStream = FileSystem.GetFileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, true);

            var commandLineLogMessageBytes = Encoding.UTF8.GetBytes(commandLineLogMessage + Environment.NewLine + Environment.NewLine);
            await encodingJob.LogFileStream.WriteAsync(commandLineLogMessageBytes, 0, commandLineLogMessageBytes.Length, cancellationToken).ConfigureAwait(false);

            process.Exited += (sender, args) => OnFfMpegProcessExited(process, encodingJob);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                Logger.ErrorException("Error starting ffmpeg", ex);

                OnTranscodeFailedToStart(encodingJob.OutputFilePath, encodingJob);

                throw;
            }

            cancellationToken.Register(() => Cancel(process, encodingJob));
            
            // MUST read both stdout and stderr asynchronously or a deadlock may occurr
            process.BeginOutputReadLine();

            // Important - don't await the log task or we won't be able to kill ffmpeg when the user stops playback
            new JobLogger(Logger).StartStreamingLog(encodingJob, process.StandardError.BaseStream, encodingJob.LogFileStream);

            // Wait for the file to exist before proceeeding
			while (!FileSystem.FileExists(encodingJob.OutputFilePath) && !encodingJob.HasExited)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return encodingJob;
        }

        private void Cancel(Process process, EncodingJob job)
        {
            Logger.Info("Killing ffmpeg process for {0}", job.OutputFilePath);

            //process.Kill();
            process.StandardInput.WriteLine("q");

            job.IsCancelled = true;
        }

        /// <summary>
        /// Processes the exited.
        /// </summary>
        /// <param name="process">The process.</param>
        /// <param name="job">The job.</param>
        private void OnFfMpegProcessExited(Process process, EncodingJob job)
        {
            job.HasExited = true;

            Logger.Debug("Disposing stream resources");
            job.Dispose();

            var isSuccesful = false;

            try
            {
                var exitCode = process.ExitCode;
                Logger.Info("FFMpeg exited with code {0}", exitCode);

                isSuccesful = exitCode == 0;
            }
            catch
            {
                Logger.Error("FFMpeg exited with an error.");
            }

            if (isSuccesful && !job.IsCancelled)
            {
                job.TaskCompletionSource.TrySetResult(true);
            }
            else if (job.IsCancelled)
            {
                try
                {
                    DeleteFiles(job);
                }
                catch
                {
                }
                try
                {
                    job.TaskCompletionSource.TrySetException(new OperationCanceledException());
                }
                catch
                {
                }
            }
            else
            {
                try
                {
                    DeleteFiles(job);
                }
                catch
                {
                }
                try
                {
                    job.TaskCompletionSource.TrySetException(new ApplicationException("Encoding failed"));
                }
                catch
                {
                }
            }

            // This causes on exited to be called twice:
            //try
            //{
            //    // Dispose the process
            //    process.Dispose();
            //}
            //catch (Exception ex)
            //{
            //    Logger.ErrorException("Error disposing ffmpeg.", ex);
            //}
        }

        protected virtual void DeleteFiles(EncodingJob job)
        {
            FileSystem.DeleteFile(job.OutputFilePath);
        }

        private void OnTranscodeBeginning(EncodingJob job)
        {
            job.ReportTranscodingProgress(null, null, null, null);
        }

        private void OnTranscodeFailedToStart(string path, EncodingJob job)
        {
            if (!string.IsNullOrWhiteSpace(job.Options.DeviceId))
            {
                SessionManager.ClearTranscodingInfo(job.Options.DeviceId);
            }
        }

        protected abstract bool IsVideoEncoder { get; }

        protected virtual string GetWorkingDirectory(EncodingJobOptions options)
        {
            return null;
        }

        protected EncodingOptions GetEncodingOptions()
        {
            return ConfigurationManager.GetConfiguration<EncodingOptions>("encoding");
        }

        protected abstract string GetCommandLineArguments(EncodingJob job);

        private string GetOutputFilePath(EncodingJob state)
        {
            var folder = string.IsNullOrWhiteSpace(state.Options.OutputDirectory) ? 
                ConfigurationManager.ApplicationPaths.TranscodingTempPath :
                state.Options.OutputDirectory;

            var outputFileExtension = GetOutputFileExtension(state);

            var filename = state.Id + (outputFileExtension ?? string.Empty).ToLower();
            return Path.Combine(folder, filename);
        }

        protected virtual string GetOutputFileExtension(EncodingJob state)
        {
            if (!string.IsNullOrWhiteSpace(state.Options.OutputContainer))
            {
                return "." + state.Options.OutputContainer;
            }

            return null;
        }

        /// <summary>
        /// Gets the number of threads.
        /// </summary>
        /// <returns>System.Int32.</returns>
        protected int GetNumberOfThreads(EncodingJob job, bool isWebm)
        {
            return job.Options.CpuCoreLimit ?? 0;
        }

        protected string GetInputModifier(EncodingJob job, bool genPts = true)
        {
            var inputModifier = string.Empty;

            var probeSize = GetProbeSizeArgument(job);
            inputModifier += " " + probeSize;
            inputModifier = inputModifier.Trim();

            var userAgentParam = GetUserAgentParam(job);

            if (!string.IsNullOrWhiteSpace(userAgentParam))
            {
                inputModifier += " " + userAgentParam;
            }

            inputModifier = inputModifier.Trim();

            inputModifier += " " + GetFastSeekCommandLineParameter(job.Options);
            inputModifier = inputModifier.Trim();

            if (job.IsVideoRequest && genPts)
            {
                inputModifier += " -fflags +genpts";
            }

            if (!string.IsNullOrEmpty(job.InputAudioSync))
            {
                inputModifier += " -async " + job.InputAudioSync;
            }

            if (!string.IsNullOrEmpty(job.InputVideoSync))
            {
                inputModifier += " -vsync " + job.InputVideoSync;
            }

            if (job.ReadInputAtNativeFramerate)
            {
                inputModifier += " -re";
            }

            var videoDecoder = GetVideoDecoder(job);
            if (!string.IsNullOrWhiteSpace(videoDecoder))
            {
                inputModifier += " " + videoDecoder;
            }

            return inputModifier;
        }

        /// <summary>
        /// Gets the name of the output video codec
        /// </summary>
        /// <param name="state">The state.</param>
        /// <returns>System.String.</returns>
        protected string GetVideoDecoder(EncodingJob state)
        {
            if (string.Equals(GetEncodingOptions().HardwareAccelerationType, "qsv", StringComparison.OrdinalIgnoreCase))
            {
                if (state.VideoStream != null && !string.IsNullOrWhiteSpace(state.VideoStream.Codec))
                {
                    switch (state.MediaSource.VideoStream.Codec.ToLower())
                    {
                        case "avc":
                        case "h264":
                            if (MediaEncoder.SupportsDecoder("h264_qsv"))
                            {
                                return "-c:v h264_qsv ";
                            }
                            break;
                        case "mpeg2video":
                            if (MediaEncoder.SupportsDecoder("mpeg2_qsv"))
                            {
                                return "-c:v mpeg2_qsv ";
                            }
                            break;
                        case "vc1":
                            if (MediaEncoder.SupportsDecoder("vc1_qsv"))
                            {
                                return "-c:v vc1_qsv ";
                            }
                            break;
                    }
                }
            }

            // leave blank so ffmpeg will decide
            return null;
        }

        private string GetUserAgentParam(EncodingJob job)
        {
            string useragent = null;

            job.RemoteHttpHeaders.TryGetValue("User-Agent", out useragent);

            if (!string.IsNullOrWhiteSpace(useragent))
            {
                return "-user-agent \"" + useragent + "\"";
            }

            return string.Empty;
        }

        /// <summary>
        /// Gets the probe size argument.
        /// </summary>
        /// <param name="job">The job.</param>
        /// <returns>System.String.</returns>
        private string GetProbeSizeArgument(EncodingJob job)
        {
            if (job.PlayableStreamFileNames.Count > 0)
            {
                return MediaEncoder.GetProbeSizeArgument(job.PlayableStreamFileNames.ToArray(), job.InputProtocol);
            }

            return MediaEncoder.GetProbeSizeArgument(new[] { job.MediaPath }, job.InputProtocol);
        }

        /// <summary>
        /// Gets the fast seek command line parameter.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>System.String.</returns>
        /// <value>The fast seek command line parameter.</value>
        protected string GetFastSeekCommandLineParameter(EncodingJobOptions options)
        {
            var time = options.StartTimeTicks;

            if (time.HasValue && time.Value > 0)
            {
                return string.Format("-ss {0}", MediaEncoder.GetTimeParameter(time.Value));
            }

            return string.Empty;
        }

        /// <summary>
        /// Gets the input argument.
        /// </summary>
        /// <param name="job">The job.</param>
        /// <returns>System.String.</returns>
        protected string GetInputArgument(EncodingJob job)
        {
            var arg = "-i " + GetInputPathArgument(job);

            if (job.SubtitleStream != null)
            {
                if (job.SubtitleStream.IsExternal && !job.SubtitleStream.IsTextSubtitleStream)
                {
                    arg += " -i \"" + job.SubtitleStream.Path + "\"";
                }
            }

            return arg;
        }

        private string GetInputPathArgument(EncodingJob job)
        {
            var protocol = job.InputProtocol;

            var inputPath = new[] { job.MediaPath };

            if (job.IsInputVideo)
            {
                if (!(job.VideoType == VideoType.Iso && job.IsoMount == null))
                {
                    inputPath = MediaEncoderHelpers.GetInputArgument(FileSystem, job.MediaPath, job.InputProtocol, job.IsoMount, job.PlayableStreamFileNames);
                }
            }

            return MediaEncoder.GetInputArgument(inputPath, protocol);
        }

        private async Task AcquireResources(EncodingJob state, CancellationToken cancellationToken)
        {
            if (state.VideoType == VideoType.Iso && state.IsoType.HasValue && IsoManager.CanMount(state.MediaPath))
            {
                state.IsoMount = await IsoManager.Mount(state.MediaPath, cancellationToken).ConfigureAwait(false);
            }

            if (state.MediaSource.RequiresOpening && string.IsNullOrWhiteSpace(state.LiveStreamId))
            {
                var liveStreamResponse = await MediaSourceManager.OpenLiveStream(new LiveStreamRequest
                {
                    OpenToken = state.MediaSource.OpenToken

                }, false, cancellationToken).ConfigureAwait(false);

                AttachMediaStreamInfo(state, liveStreamResponse.MediaSource, state.Options);

                if (state.IsVideoRequest)
                {
                    EncodingJobFactory.TryStreamCopy(state, state.Options);
                }
            }

            if (state.MediaSource.BufferMs.HasValue)
            {
                await Task.Delay(state.MediaSource.BufferMs.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        private void AttachMediaStreamInfo(EncodingJob state,
          MediaSourceInfo mediaSource,
          EncodingJobOptions videoRequest)
        {
            EncodingJobFactory.AttachMediaStreamInfo(state, mediaSource, videoRequest);
        }

        /// <summary>
        /// Gets the internal graphical subtitle param.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="outputVideoCodec">The output video codec.</param>
        /// <returns>System.String.</returns>
        protected string GetGraphicalSubtitleParam(EncodingJob state, string outputVideoCodec)
        {
            var outputSizeParam = string.Empty;

            var request = state.Options;

            // Add resolution params, if specified
            if (request.Width.HasValue || request.Height.HasValue || request.MaxHeight.HasValue || request.MaxWidth.HasValue)
            {
                outputSizeParam = GetOutputSizeParam(state, outputVideoCodec).TrimEnd('"');
                outputSizeParam = "," + outputSizeParam.Substring(outputSizeParam.IndexOf("scale", StringComparison.OrdinalIgnoreCase));
            }

            var videoSizeParam = string.Empty;

            if (state.VideoStream != null && state.VideoStream.Width.HasValue && state.VideoStream.Height.HasValue)
            {
                videoSizeParam = string.Format(",scale={0}:{1}", state.VideoStream.Width.Value.ToString(UsCulture), state.VideoStream.Height.Value.ToString(UsCulture));
            }

            var mapPrefix = state.SubtitleStream.IsExternal ?
                1 :
                0;

            var subtitleStreamIndex = state.SubtitleStream.IsExternal
                ? 0
                : state.SubtitleStream.Index;

            return string.Format(" -filter_complex \"[{0}:{1}]format=yuva444p{4},lut=u=128:v=128:y=gammaval(.3)[sub] ; [0:{2}] [sub] overlay{3}\"",
                mapPrefix.ToString(UsCulture),
                subtitleStreamIndex.ToString(UsCulture),
                state.VideoStream.Index.ToString(UsCulture),
                outputSizeParam,
                videoSizeParam);
        }

        /// <summary>
        /// Gets the video bitrate to specify on the command line
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="videoCodec">The video codec.</param>
        /// <param name="isHls">if set to <c>true</c> [is HLS].</param>
        /// <returns>System.String.</returns>
        protected string GetVideoQualityParam(EncodingJob state, string videoCodec, bool isHls)
        {
            var param = string.Empty;

            var isVc1 = state.VideoStream != null &&
                string.Equals(state.VideoStream.Codec, "vc1", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(videoCodec, "libx264", StringComparison.OrdinalIgnoreCase))
            {
                param = "-preset superfast";

                param += " -crf 28";
            }

            else if (string.Equals(videoCodec, "libx265", StringComparison.OrdinalIgnoreCase))
            {
                param = "-preset fast";

                param += " -crf 28";
            }

            // webm
            else if (string.Equals(videoCodec, "libvpx", StringComparison.OrdinalIgnoreCase))
            {
                // Values 0-3, 0 being highest quality but slower
                var profileScore = 0;

                string crf;
                var qmin = "0";
                var qmax = "50";

                crf = "10";

                if (isVc1)
                {
                    profileScore++;
                }

                // Max of 2
                profileScore = Math.Min(profileScore, 2);

                // http://www.webmproject.org/docs/encoder-parameters/
                param = string.Format("-speed 16 -quality good -profile:v {0} -slices 8 -crf {1} -qmin {2} -qmax {3}",
                    profileScore.ToString(UsCulture),
                    crf,
                    qmin,
                    qmax);
            }

            else if (string.Equals(videoCodec, "mpeg4", StringComparison.OrdinalIgnoreCase))
            {
                param = "-mbd rd -flags +mv4+aic -trellis 2 -cmp 2 -subcmp 2 -bf 2";
            }

            // asf/wmv
            else if (string.Equals(videoCodec, "wmv2", StringComparison.OrdinalIgnoreCase))
            {
                param = "-qmin 2";
            }

            else if (string.Equals(videoCodec, "msmpeg4", StringComparison.OrdinalIgnoreCase))
            {
                param = "-mbd 2";
            }

            param += GetVideoBitrateParam(state, videoCodec, isHls);

            var framerate = GetFramerateParam(state);
            if (framerate.HasValue)
            {
                param += string.Format(" -r {0}", framerate.Value.ToString(UsCulture));
            }

            if (!string.IsNullOrEmpty(state.OutputVideoSync))
            {
                param += " -vsync " + state.OutputVideoSync;
            }

            if (!string.IsNullOrEmpty(state.Options.Profile))
            {
                param += " -profile:v " + state.Options.Profile;
            }

            if (state.Options.Level.HasValue)
            {
                param += " -level " + state.Options.Level.Value.ToString(UsCulture);
            }

            return "-pix_fmt yuv420p " + param;
        }

        protected string GetVideoBitrateParam(EncodingJob state, string videoCodec, bool isHls)
        {
            var bitrate = state.OutputVideoBitrate;

            if (bitrate.HasValue)
            {
                var hasFixedResolution = state.Options.HasFixedResolution;

                if (string.Equals(videoCodec, "libvpx", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasFixedResolution)
                    {
                        return string.Format(" -minrate:v ({0}*.90) -maxrate:v ({0}*1.10) -bufsize:v {0} -b:v {0}", bitrate.Value.ToString(UsCulture));
                    }

                    // With vpx when crf is used, b:v becomes a max rate
                    // https://trac.ffmpeg.org/wiki/vpxEncodingGuide. But higher bitrate source files -b:v causes judder so limite the bitrate but dont allow it to "saturate" the bitrate. So dont contrain it down just up.
                    return string.Format(" -maxrate:v {0} -bufsize:v ({0}*2) -b:v {0}", bitrate.Value.ToString(UsCulture));
                }

                if (string.Equals(videoCodec, "msmpeg4", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Format(" -b:v {0}", bitrate.Value.ToString(UsCulture));
                }

                // H264
                if (hasFixedResolution)
                {
                    if (isHls)
                    {
                        return string.Format(" -b:v {0} -maxrate ({0}*.80) -bufsize {0}", bitrate.Value.ToString(UsCulture));
                    }

                    return string.Format(" -b:v {0}", bitrate.Value.ToString(UsCulture));
                }

                return string.Format(" -maxrate {0} -bufsize {1}",
                    bitrate.Value.ToString(UsCulture),
                    (bitrate.Value * 2).ToString(UsCulture));
            }

            return string.Empty;
        }

        protected double? GetFramerateParam(EncodingJob state)
        {
            if (state.Options.Framerate.HasValue)
            {
                return state.Options.Framerate.Value;
            }

            var maxrate = state.Options.MaxFramerate;

            if (maxrate.HasValue && state.VideoStream != null)
            {
                var contentRate = state.VideoStream.AverageFrameRate ?? state.VideoStream.RealFrameRate;

                if (contentRate.HasValue && contentRate.Value > maxrate.Value)
                {
                    return maxrate;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the map args.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <returns>System.String.</returns>
        protected virtual string GetMapArgs(EncodingJob state)
        {
            // If we don't have known media info
            // If input is video, use -sn to drop subtitles
            // Otherwise just return empty
            if (state.VideoStream == null && state.AudioStream == null)
            {
                return state.IsInputVideo ? "-sn" : string.Empty;
            }

            // We have media info, but we don't know the stream indexes
            if (state.VideoStream != null && state.VideoStream.Index == -1)
            {
                return "-sn";
            }

            // We have media info, but we don't know the stream indexes
            if (state.AudioStream != null && state.AudioStream.Index == -1)
            {
                return state.IsInputVideo ? "-sn" : string.Empty;
            }

            var args = string.Empty;

            if (state.VideoStream != null)
            {
                args += string.Format("-map 0:{0}", state.VideoStream.Index);
            }
            else
            {
                args += "-map -0:v";
            }

            if (state.AudioStream != null)
            {
                args += string.Format(" -map 0:{0}", state.AudioStream.Index);
            }

            else
            {
                args += " -map -0:a";
            }

            if (state.SubtitleStream == null)
            {
                args += " -map -0:s";
            }
            else if (state.SubtitleStream.IsExternal && !state.SubtitleStream.IsTextSubtitleStream)
            {
                args += " -map 1:0 -sn";
            }

            return args;
        }

        /// <summary>
        /// Determines whether the specified stream is H264.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <returns><c>true</c> if the specified stream is H264; otherwise, <c>false</c>.</returns>
        protected bool IsH264(MediaStream stream)
        {
            var codec = stream.Codec ?? string.Empty;

            return codec.IndexOf("264", StringComparison.OrdinalIgnoreCase) != -1 ||
                   codec.IndexOf("avc", StringComparison.OrdinalIgnoreCase) != -1;
        }

        /// <summary>
        /// If we're going to put a fixed size on the command line, this will calculate it
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="outputVideoCodec">The output video codec.</param>
        /// <param name="allowTimeStampCopy">if set to <c>true</c> [allow time stamp copy].</param>
        /// <returns>System.String.</returns>
        protected string GetOutputSizeParam(EncodingJob state,
            string outputVideoCodec,
            bool allowTimeStampCopy = true)
        {
            // http://sonnati.wordpress.com/2012/10/19/ffmpeg-the-swiss-army-knife-of-internet-streaming-part-vi/

            var request = state.Options;

            var filters = new List<string>();

            if (state.DeInterlace)
            {
                filters.Add("yadif=0:-1:0");
            }

            // If fixed dimensions were supplied
            if (request.Width.HasValue && request.Height.HasValue)
            {
                var widthParam = request.Width.Value.ToString(UsCulture);
                var heightParam = request.Height.Value.ToString(UsCulture);

                filters.Add(string.Format("scale=trunc({0}/2)*2:trunc({1}/2)*2", widthParam, heightParam));
            }

            // If Max dimensions were supplied, for width selects lowest even number between input width and width req size and selects lowest even number from in width*display aspect and requested size
            else if (request.MaxWidth.HasValue && request.MaxHeight.HasValue)
            {
                var maxWidthParam = request.MaxWidth.Value.ToString(UsCulture);
                var maxHeightParam = request.MaxHeight.Value.ToString(UsCulture);

                filters.Add(string.Format("scale=trunc(min(max(iw\\,ih*dar)\\,min({0}\\,{1}*dar))/2)*2:trunc(min(max(iw/dar\\,ih)\\,min({0}/dar\\,{1}))/2)*2", maxWidthParam, maxHeightParam));
            }

            // If a fixed width was requested
            else if (request.Width.HasValue)
            {
                var widthParam = request.Width.Value.ToString(UsCulture);

                filters.Add(string.Format("scale={0}:trunc(ow/a/2)*2", widthParam));
            }

            // If a fixed height was requested
            else if (request.Height.HasValue)
            {
                var heightParam = request.Height.Value.ToString(UsCulture);

                filters.Add(string.Format("scale=trunc(oh*a/2)*2:{0}", heightParam));
            }

            // If a max width was requested
            else if (request.MaxWidth.HasValue)
            {
                var maxWidthParam = request.MaxWidth.Value.ToString(UsCulture);

                filters.Add(string.Format("scale=min(iw\\,{0}):trunc(ow/dar/2)*2", maxWidthParam));
            }

            // If a max height was requested
            else if (request.MaxHeight.HasValue)
            {
                var maxHeightParam = request.MaxHeight.Value.ToString(UsCulture);

                filters.Add(string.Format("scale=trunc(oh*a/2)*2:min(ih\\,{0})", maxHeightParam));
            }

            var output = string.Empty;

            if (state.SubtitleStream != null && state.SubtitleStream.IsTextSubtitleStream)
            {
                var subParam = GetTextSubtitleParam(state);

                filters.Add(subParam);

                if (allowTimeStampCopy)
                {
                    output += " -copyts";
                }
            }

            if (filters.Count > 0)
            {
                output += string.Format(" -vf \"{0}\"", string.Join(",", filters.ToArray()));
            }

            return output;
        }

        /// <summary>
        /// Gets the text subtitle param.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <returns>System.String.</returns>
        protected string GetTextSubtitleParam(EncodingJob state)
        {
            var seconds = Math.Round(TimeSpan.FromTicks(state.Options.StartTimeTicks ?? 0).TotalSeconds);

            if (state.SubtitleStream.IsExternal)
            {
                var subtitlePath = state.SubtitleStream.Path;

                var charsetParam = string.Empty;

                if (!string.IsNullOrEmpty(state.SubtitleStream.Language))
                {
                    var charenc = SubtitleEncoder.GetSubtitleFileCharacterSet(subtitlePath, state.MediaSource.Protocol, CancellationToken.None).Result;

                    if (!string.IsNullOrEmpty(charenc))
                    {
                        charsetParam = ":charenc=" + charenc;
                    }
                }

                // TODO: Perhaps also use original_size=1920x800 ??
                return string.Format("subtitles=filename='{0}'{1},setpts=PTS -{2}/TB",
                    MediaEncoder.EscapeSubtitleFilterPath(subtitlePath),
                    charsetParam,
                    seconds.ToString(UsCulture));
            }

            return string.Format("subtitles='{0}:si={1}',setpts=PTS -{2}/TB",
                MediaEncoder.EscapeSubtitleFilterPath(state.MediaPath),
                state.InternalSubtitleStreamOffset.ToString(UsCulture),
                seconds.ToString(UsCulture));
        }

        protected string GetAudioFilterParam(EncodingJob state, bool isHls)
        {
            var volParam = string.Empty;
            var audioSampleRate = string.Empty;

            var channels = state.OutputAudioChannels;

            // Boost volume to 200% when downsampling from 6ch to 2ch
            if (channels.HasValue && channels.Value <= 2)
            {
                if (state.AudioStream != null && state.AudioStream.Channels.HasValue && state.AudioStream.Channels.Value > 5)
                {
                    volParam = ",volume=" + GetEncodingOptions().DownMixAudioBoost.ToString(UsCulture);
                }
            }

            if (state.OutputAudioSampleRate.HasValue)
            {
                audioSampleRate = state.OutputAudioSampleRate.Value + ":";
            }

            var adelay = isHls ? "adelay=1," : string.Empty;

            var pts = string.Empty;

            if (state.SubtitleStream != null && state.SubtitleStream.IsTextSubtitleStream)
            {
                var seconds = TimeSpan.FromTicks(state.Options.StartTimeTicks ?? 0).TotalSeconds;

                pts = string.Format(",asetpts=PTS-{0}/TB", Math.Round(seconds).ToString(UsCulture));
            }

            return string.Format("-af \"{0}aresample={1}async={4}{2}{3}\"",

                adelay,
                audioSampleRate,
                volParam,
                pts,
                state.OutputAudioSync);
        }
    }
}
