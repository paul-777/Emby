﻿using MediaBrowser.Model.Logging;
using ServiceStack.Web;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;

namespace MediaBrowser.Server.Implementations.HttpServer
{
    public class RangeRequestWriter : IStreamWriter, IHttpResult
    {
        /// <summary>
        /// Gets or sets the source stream.
        /// </summary>
        /// <value>The source stream.</value>
        private Stream SourceStream { get; set; }
        private string RangeHeader { get; set; }
        private bool IsHeadRequest { get; set; }

        private long RangeStart { get; set; }
        private long RangeEnd { get; set; }
        private long RangeLength { get; set; }
        private long TotalContentLength { get; set; }

        public Action OnComplete { get; set; }
        private readonly ILogger _logger;

        // 256k
        private const int BufferSize = 262144;

        /// <summary>
        /// The _options
        /// </summary>
        private readonly Dictionary<string, string> _options = new Dictionary<string, string>();

        /// <summary>
        /// The us culture
        /// </summary>
        private static readonly CultureInfo UsCulture = new CultureInfo("en-US");

        /// <summary>
        /// Additional HTTP Headers
        /// </summary>
        /// <value>The headers.</value>
        public Dictionary<string, string> Headers
        {
            get { return _options; }
        }

        /// <summary>
        /// Gets the options.
        /// </summary>
        /// <value>The options.</value>
        public IDictionary<string, string> Options
        {
            get { return Headers; }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamWriter" /> class.
        /// </summary>
        /// <param name="rangeHeader">The range header.</param>
        /// <param name="source">The source.</param>
        /// <param name="contentType">Type of the content.</param>
        /// <param name="isHeadRequest">if set to <c>true</c> [is head request].</param>
        public RangeRequestWriter(string rangeHeader, Stream source, string contentType, bool isHeadRequest, ILogger logger)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                throw new ArgumentNullException("contentType");
            }

            RangeHeader = rangeHeader;
            SourceStream = source;
            IsHeadRequest = isHeadRequest;
            this._logger = logger;

            ContentType = contentType;
            Options["Content-Type"] = contentType;
            Options["Accept-Ranges"] = "bytes";
            StatusCode = HttpStatusCode.PartialContent;

            SetRangeValues();
        }

        /// <summary>
        /// Sets the range values.
        /// </summary>
        private void SetRangeValues()
        {
            var requestedRange = RequestedRanges[0];

            TotalContentLength = SourceStream.Length;

            // If the requested range is "0-", we can optimize by just doing a stream copy
            if (!requestedRange.Value.HasValue)
            {
                RangeEnd = TotalContentLength - 1;
            }
            else
            {
                RangeEnd = requestedRange.Value.Value;
            }

            RangeStart = requestedRange.Key;
            RangeLength = 1 + RangeEnd - RangeStart;

            // Content-Length is the length of what we're serving, not the original content
            Options["Content-Length"] = RangeLength.ToString(UsCulture);
            Options["Content-Range"] = string.Format("bytes {0}-{1}/{2}", RangeStart, RangeEnd, TotalContentLength);

            if (RangeStart > 0)
            {
                SourceStream.Position = RangeStart;
            }
        }

        /// <summary>
        /// The _requested ranges
        /// </summary>
        private List<KeyValuePair<long, long?>> _requestedRanges;
        /// <summary>
        /// Gets the requested ranges.
        /// </summary>
        /// <value>The requested ranges.</value>
        protected List<KeyValuePair<long, long?>> RequestedRanges
        {
            get
            {
                if (_requestedRanges == null)
                {
                    _requestedRanges = new List<KeyValuePair<long, long?>>();

                    // Example: bytes=0-,32-63
                    var ranges = RangeHeader.Split('=')[1].Split(',');

                    foreach (var range in ranges)
                    {
                        var vals = range.Split('-');

                        long start = 0;
                        long? end = null;

                        if (!string.IsNullOrEmpty(vals[0]))
                        {
                            start = long.Parse(vals[0], UsCulture);
                        }
                        if (!string.IsNullOrEmpty(vals[1]))
                        {
                            end = long.Parse(vals[1], UsCulture);
                        }

                        _requestedRanges.Add(new KeyValuePair<long, long?>(start, end));
                    }
                }

                return _requestedRanges;
            }
        }

        /// <summary>
        /// Writes to.
        /// </summary>
        /// <param name="responseStream">The response stream.</param>
        public void WriteTo(Stream responseStream)
        {
            WriteToInternal(responseStream);
        }

        /// <summary>
        /// Writes to async.
        /// </summary>
        /// <param name="responseStream">The response stream.</param>
        /// <returns>Task.</returns>
        private void WriteToInternal(Stream responseStream)
        {
            try
            {
                // Headers only
                if (IsHeadRequest)
                {
                    return;
                }

                using (var source = SourceStream)
                {
                    // If the requested range is "0-", we can optimize by just doing a stream copy
                    if (RangeEnd >= TotalContentLength - 1)
                    {
                        source.CopyTo(responseStream, BufferSize);
                    }
                    else
                    {
                        CopyToInternal(source, responseStream, RangeLength);
                    }
                }
            }
            catch (IOException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error in range request writer", ex);
                throw;
            }
            finally
            {
                if (OnComplete != null)
                {
                    OnComplete();
                }
            }
        }

        private void CopyToInternal(Stream source, Stream destination, long copyLength)
        {
            var array = new byte[BufferSize];
            int count;
            while ((count = source.Read(array, 0, array.Length)) != 0)
            {
                var bytesToCopy = Math.Min(count, copyLength);

                destination.Write(array, 0, Convert.ToInt32(bytesToCopy));

                copyLength -= bytesToCopy;

                if (copyLength <= 0)
                {
                    break;
                }
            }
        }

        public string ContentType { get; set; }

        public IRequest RequestContext { get; set; }

        public object Response { get; set; }

        public IContentTypeWriter ResponseFilter { get; set; }

        public int Status { get; set; }

        public HttpStatusCode StatusCode
        {
            get { return (HttpStatusCode)Status; }
            set { Status = (int)value; }
        }

        public string StatusDescription { get; set; }

        public int PaddingLength { get; set; }
    }
}