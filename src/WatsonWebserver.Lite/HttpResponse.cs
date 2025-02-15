﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CavemanTcp;
using WatsonWebserver.Core;

namespace WatsonWebserver.Lite
{
    /// <summary>
    /// Response to an HTTP request.
    /// </summary>
    public class HttpResponse : HttpResponseBase
    {
        #region Public-Members

        /// <summary>
        /// Retrieve the response body sent using a Send() or Send() method.
        /// </summary>
        [JsonIgnore]
        public override string DataAsString
        {
            get
            {
                if (_DataAsBytes != null) return Encoding.UTF8.GetString(_DataAsBytes);
                if (_Data != null && ContentLength > 0)
                {
                    _DataAsBytes = ReadStreamFully(_Data);
                    if (_DataAsBytes != null) return Encoding.UTF8.GetString(_DataAsBytes);
                }
                return null;
            }
        }

        /// <summary>
        /// Retrieve the response body sent using a Send() or Send() method.
        /// </summary>
        [JsonIgnore]
        public override byte[] DataAsBytes
        {
            get
            {
                if (_DataAsBytes != null) return _DataAsBytes;
                if (_Data != null && ContentLength > 0)
                {
                    _DataAsBytes = ReadStreamFully(_Data);
                    return _DataAsBytes;
                }
                return null;
            }
        }

        /// <summary>
        /// Response data stream sent to the requestor.
        /// </summary>
        [JsonIgnore]
        public override MemoryStream Data
        {
            get
            {
                return _Data;
            }
        }

        #endregion

        #region Private-Members

        private bool _HeadersSet = false;
        private bool _HeadersSent = false;
        private byte[] _DataAsBytes = null;
        private MemoryStream _Data = null;
        private string _IpPort;
        private WebserverSettings.HeaderSettings _HeaderSettings = null;
        private int _StreamBufferSize = 65536;
        private NameValueCollection _Headers = new NameValueCollection(StringComparer.InvariantCultureIgnoreCase);
        private Stream _Stream;
        private HttpRequestBase _Request;  
        private WebserverEvents _Events = new WebserverEvents();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the object.
        /// </summary>
        public HttpResponse()
        {

        }

        internal HttpResponse(
            string ipPort, 
            WebserverSettings.HeaderSettings headers, 
            Stream stream, 
            HttpRequestBase req, 
            WebserverEvents events, 
            int bufferSize)
        {
            if (String.IsNullOrEmpty(ipPort)) throw new ArgumentNullException(nameof(ipPort));
            if (headers == null) throw new ArgumentNullException(nameof(headers));
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (events == null) throw new ArgumentNullException(nameof(events));

            ProtocolVersion = req.ProtocolVersion;

            _IpPort = ipPort;
            _HeaderSettings = headers;
            _Request = req;
            _Stream = stream;
            _Events = events;
            _StreamBufferSize = bufferSize; 
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Send headers with a specified content length and no data to the requestor and terminate the connection.  Useful for HEAD requests where the content length must be set.
        /// </summary> 
        /// <param name="token">Cancellation token for canceling the request.</param>
        public override async Task<bool> Send(CancellationToken token = default)
        {
            if (ChunkedTransfer) throw new IOException("Response is configured to use chunked transfer-encoding.  Use SendChunk() and SendFinalChunk().");
            return await SendInternalAsync(0, null, true, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send headers with a specified content length and no data to the requestor and terminate the connection.  Useful for HEAD requests where the content length must be set.
        /// </summary> 
        /// <param name="contentLength">Value to set in Content-Length header.</param>
        /// <param name="token">Cancellation token for canceling the request.</param>
        public override async Task<bool> Send(long contentLength, CancellationToken token = default)
        {
            if (ChunkedTransfer) throw new IOException("Response is configured to use chunked transfer-encoding.  Use SendChunk() and SendFinalChunk().");
            ContentLength = contentLength;
            return await SendInternalAsync(0, null, true, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send headers and data to the requestor and terminate the connection.
        /// </summary>
        /// <param name="data">Data.</param> 
        /// <param name="token">Cancellation token for canceling the request.</param>
        public override async Task<bool> Send(string data, CancellationToken token = default)
        {
            if (ChunkedTransfer) throw new IOException("Response is configured to use chunked transfer-encoding.  Use SendChunk() and SendFinalChunk().");
            if (String.IsNullOrEmpty(data))
                return await SendInternalAsync(0, null, true, token).ConfigureAwait(false);

            byte[] bytes = Encoding.UTF8.GetBytes(data);
            MemoryStream ms = new MemoryStream();
            await ms.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            ms.Seek(0, SeekOrigin.Begin);
            return await SendInternalAsync(bytes.Length, ms, true, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send headers and data to the requestor and terminate the connection.
        /// </summary>
        /// <param name="data">Data.</param> 
        /// <param name="token">Cancellation token for canceling the request.</param>
        public override async Task<bool> Send(byte[] data, CancellationToken token = default)
        {
            if (ChunkedTransfer) throw new IOException("Response is configured to use chunked transfer-encoding.  Use SendChunk() and SendFinalChunk().");
            if (data == null || data.Length < 1)
                return await SendInternalAsync(0, null, true, token).ConfigureAwait(false);

            MemoryStream ms = new MemoryStream();
            await ms.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
            ms.Seek(0, SeekOrigin.Begin); 
            return await SendInternalAsync(data.Length, ms, true, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send headers and data to the requestor and terminate the connection.
        /// </summary>
        /// <param name="contentLength">Number of bytes to read from the stream.</param>
        /// <param name="stream">Stream containing response data.</param>
        /// <param name="token">Cancellation token for canceling the request.</param>
        public override async Task<bool> Send(long contentLength, Stream stream, CancellationToken token = default)
        {
            if (ChunkedTransfer) throw new IOException("Response is configured to use chunked transfer-encoding.  Use SendChunk() and SendFinalChunk().");
            if (contentLength <= 0 || stream == null || !stream.CanRead)
                return await SendInternalAsync(0, null, true, token).ConfigureAwait(false);

            return await SendInternalAsync(contentLength, stream, true, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send headers (if not already sent) and a chunk of data using chunked transfer-encoding, and keep the connection in-tact.
        /// </summary>
        /// <param name="chunk">Chunk of data.</param>
        /// <param name="token">Cancellation token useful for canceling the request.</param>
        /// <returns>True if successful.</returns>
        public override async Task<bool> SendChunk(byte[] chunk, CancellationToken token = default)
        {
            if (!ChunkedTransfer) throw new IOException("Response is not configured to use chunked transfer-encoding.  Set ChunkedTransfer to true first, otherwise use Send().");
            if (!_HeadersSet) SetDefaultHeaders();

            if (chunk != null && chunk.Length > 0)
                ContentLength += chunk.Length;

            try
            {
                if (chunk == null || chunk.Length < 1) chunk = new byte[0];

                byte[] chunkBytes = new byte[0];

                using (MemoryStream ms = new MemoryStream())
                {
                    chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes(Convert.ToString(chunk.Length, 16)));
                    chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes("\r\n"));

                    chunkBytes = AppendBytes(chunkBytes, chunk);
                    chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes("\r\n"));

                    await ms.WriteAsync(chunkBytes, 0, chunkBytes.Length, token).ConfigureAwait(false);
                    ms.Seek(0, SeekOrigin.Begin);

                    await SendInternalAsync(chunkBytes.Length, ms, false, token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Send headers (if not already sent) and the final chunk of data using chunked transfer-encoding and terminate the connection.
        /// </summary>
        /// <param name="chunk">Chunk of data.</param>
        /// <param name="token">Cancellation token useful for canceling the request.</param>
        /// <returns>True if successful.</returns>
        public override async Task<bool> SendFinalChunk(byte[] chunk, CancellationToken token = default)
        {
            if (!ChunkedTransfer) throw new IOException("Response is not configured to use chunked transfer-encoding.  Set ChunkedTransfer to true first, otherwise use Send().");
            if (!_HeadersSet) SetDefaultHeaders();

            if (chunk != null && chunk.Length > 0)
                ContentLength += chunk.Length;

            try
            {
                if (chunk == null || chunk.Length < 1) chunk = new byte[0];

                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] chunkBytes = AppendBytes(new byte[0], new byte[0]);

                    if (chunk.Length > 0)
                    {
                        chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes(Convert.ToString(chunk.Length, 16)));
                        chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes("\r\n"));

                        chunkBytes = AppendBytes(chunkBytes, chunk);
                        chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes("\r\n"));
                    }

                    chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes("0"));
                    chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes("\r\n"));
                    chunkBytes = AppendBytes(chunkBytes, Encoding.UTF8.GetBytes("\r\n"));
                    
                    await ms.WriteAsync(chunkBytes, 0, chunkBytes.Length, token).ConfigureAwait(false);
                    ms.Seek(0, SeekOrigin.Begin);

                    await SendInternalAsync(chunkBytes.Length, ms, true, token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Close the connection.
        /// </summary>
        public void Close()
        {
            SendInternalAsync(0, null, true).Wait();
            ResponseSent = true;
        }

        #endregion

        #region Private-Methods

        private byte[] GetHeaderBytes()
        {
            byte[] ret = new byte[0];

            ret = AppendBytes(ret, Encoding.UTF8.GetBytes(ProtocolVersion + " " + StatusCode + " " + StatusDescription + "\r\n"));

            bool contentTypeSet = false;
            if (!String.IsNullOrEmpty(ContentType))
            {
                ret = AppendBytes(ret, Encoding.UTF8.GetBytes(WebserverConstants.HeaderContentType + ": " + ContentType + "\r\n"));
                contentTypeSet = true;
            }

            bool contentLengthSet = false;
            if (!ChunkedTransfer && ContentLength >= 0)
            {
                ret = AppendBytes(ret, Encoding.UTF8.GetBytes(WebserverConstants.HeaderContentLength + ": " + ContentLength + "\r\n"));
                contentLengthSet = true;
            }

            bool transferEncodingSet = false;
            if (ChunkedTransfer)
            {
                ret = AppendBytes(ret, Encoding.UTF8.GetBytes(WebserverConstants.HeaderTransferEncoding + ": chunked\r\n"));
                transferEncodingSet = true;
            }

            ret = AppendBytes(
                ret, 
                Encoding.UTF8.GetBytes(WebserverConstants.HeaderDate + ": " + DateTime.UtcNow.ToString(WebserverConstants.HeaderDateValueFormat) + "\r\n"));
            
            for (int i = 0; i < _Headers.Count; i++)
            {
                string header = _Headers.GetKey(i);
                if (String.IsNullOrEmpty(header)) continue;
                if (contentTypeSet && header.ToLower().Equals(WebserverConstants.HeaderContentType.ToLower())) continue;
                if (contentLengthSet && header.ToLower().Equals(WebserverConstants.HeaderContentLength.ToLower())) continue;
                if (transferEncodingSet && header.ToLower().Equals(WebserverConstants.HeaderTransferEncoding.ToLower())) continue;
                if (header.ToLower().Equals(WebserverConstants.HeaderDate.ToLower())) continue;

                string[] vals = _Headers.GetValues(i);
                if (vals != null && vals.Length > 0)
                {
                    foreach (string val in vals)
                    {
                        ret = AppendBytes(ret, Encoding.UTF8.GetBytes(header + ": " + val + "\r\n"));
                    }
                }
            }

            ret = AppendBytes(ret, Encoding.UTF8.GetBytes("\r\n"));
            return ret;
        }
         
        private string GetStatusDescription()
        {
            switch (StatusCode)
            {
                case 200:
                    return "OK";
                case 201:
                    return "Created";
                case 301:
                    return "Moved Permanently";
                case 302:
                    return "Moved Temporarily";
                case 304:
                    return "Not Modified";
                case 400:
                    return "Bad Request";
                case 401:
                    return "Unauthorized";
                case 403:
                    return "Forbidden";
                case 404:
                    return "Not Found";
                case 405:
                    return "Method Not Allowed";
                case 408:
                    return "Request Timeout";
                case 429:
                    return "Too Many Requests";
                case 500:
                    return "Internal Server Error";
                case 501:
                    return "Not Implemented";
                case 503:
                    return "Service Unavailable";
                default:
                    return "Unknown";
            }
        }

        private void SetDefaultHeaders()
        {
            if (_HeaderSettings != null && _Headers != null)
            {
                foreach (KeyValuePair<string, string> defaultHeader in _HeaderSettings.DefaultHeaders)
                {
                    string key = defaultHeader.Key;
                    string val = defaultHeader.Value;

                    if (!_Headers.AllKeys.Any(k => k.ToLower().Equals(key.ToLower())))
                    {
                        _Headers.Add(key, val);
                    }
                }
            }

            _HeadersSet = true;
        }

        private byte[] ReadStreamFully(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (!input.CanRead) throw new InvalidOperationException("Input stream is not readable");

            byte[] buffer = new byte[16 * 1024];
            using (MemoryStream ms = new MemoryStream())
            {
                int read;

                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }

                byte[] ret = ms.ToArray();
                return ret;
            }
        }

        private void SetContentLength(long contentLength)
        {
            if (_HeaderSettings.IncludeContentLength
                && !ChunkedTransfer)
            {
                if (_Headers.Count > 0)
                {
                    for (int i = 0; i < _Headers.Count; i++)
                    {
                        string val = _Headers.GetKey(i);
                        if (!String.IsNullOrEmpty(val)
                            && val.ToLower().Equals("content-length"))
                        {
                            _Headers.Remove(val);
                        }
                    }
                }

                _Headers.Add("Content-Length", contentLength.ToString());
            }
        }

        private async Task<bool> SendInternalAsync(long contentLength, Stream stream, bool close, CancellationToken token = default)
        {
            if (_HeaderSettings.IncludeContentLength
                && contentLength > 0
                && !ChunkedTransfer)
            {
                ContentLength = contentLength;
            }

            if (!_HeadersSet)
            {
                SetDefaultHeaders();
                SetContentLength(contentLength);
            }

            if (!_HeadersSent)
            {
                byte[] headers = GetHeaderBytes(); 
                await _Stream.WriteAsync(headers, 0, headers.Length, token).ConfigureAwait(false);
                await _Stream.FlushAsync(token).ConfigureAwait(false);
                _HeadersSent = true;
            }

            if (contentLength > 0 && stream != null && stream.CanRead)
            {
                long bytesRemaining = contentLength;

                byte[] buffer = new byte[_StreamBufferSize];
                int bytesToRead = _StreamBufferSize;
                int bytesRead = 0;

                while (bytesRemaining > 0)
                {
                    if (bytesRemaining > _StreamBufferSize) bytesToRead = _StreamBufferSize;
                    else bytesToRead = (int)bytesRemaining;

                    bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead, token).ConfigureAwait(false);
                    if (bytesRead > 0)
                    { 
                        await _Stream.WriteAsync(buffer, 0, bytesRead, token).ConfigureAwait(false);
                        bytesRemaining -= bytesRead;
                    }
                }
                 
                await _Stream.FlushAsync(token).ConfigureAwait(false);
            }

            if (close)
            { 
                _Stream.Close();
                ResponseSent = true;
            }

            return true;
        }

        private byte[] AppendBytes(byte[] orig, byte[] append)
        {
            if (orig == null && append == null) return null;

            byte[] ret = null;

            if (append == null)
            {
                ret = new byte[orig.Length];
                Buffer.BlockCopy(orig, 0, ret, 0, orig.Length);
                return ret;
            }

            if (orig == null)
            {
                ret = new byte[append.Length];
                Buffer.BlockCopy(append, 0, ret, 0, append.Length);
                return ret;
            }

            ret = new byte[orig.Length + append.Length];
            Buffer.BlockCopy(orig, 0, ret, 0, orig.Length);
            Buffer.BlockCopy(append, 0, ret, orig.Length, append.Length);
            return ret;
        }

        #endregion
    }
}