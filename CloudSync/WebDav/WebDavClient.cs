using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using StardewModdingAPI;

namespace CloudSync.WebDav
{
    internal class WebDavClient
    {
        private readonly HttpClient Http;
        private readonly string BaseUrl;
        private readonly IMonitor Monitor;
        private const int MaxRetries = 1;

        public WebDavClient(string nextcloudUrl, string username, string password,
                            int timeoutSeconds, IMonitor monitor)
        {
            this.Monitor = monitor;

            string host = nextcloudUrl.TrimEnd('/');
            this.BaseUrl = $"{host}/remote.php/dav/files/{Uri.EscapeDataString(username)}";

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            this.Http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };

            string credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{username}:{password}")
            );
            this.Http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }

        private string BuildUrl(string remotePath)
        {
            return $"{this.BaseUrl}/{remotePath.TrimStart('/')}";
        }

        /// <summary>Test connection by doing PROPFIND on root.</summary>
        public async Task<bool> TestConnection()
        {
            try
            {
                var resources = await this.PropFind("/");
                return resources != null;
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Connection test failed: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>Upload a file to the remote path.</summary>
        public async Task<bool> Upload(string remotePath, byte[] data)
        {
            return await this.WithRetry(async () =>
            {
                string url = this.BuildUrl(remotePath);
                using var content = new ByteArrayContent(data);
                content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/octet-stream");

                var response = await this.Http.PutAsync(url, content);

                if (response.StatusCode == HttpStatusCode.Conflict ||
                    response.StatusCode == HttpStatusCode.NotFound)
                {
                    string parent = remotePath.Substring(0,
                        remotePath.LastIndexOf('/'));
                    if (!string.IsNullOrEmpty(parent))
                    {
                        await this.CreateDirectory(parent);
                        using var retryContent = new ByteArrayContent(data);
                        retryContent.Headers.ContentType =
                            new MediaTypeHeaderValue("application/octet-stream");
                        response = await this.Http.PutAsync(url, retryContent);
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    this.Monitor.Log(
                        $"Upload failed: {response.StatusCode} for {remotePath}",
                        LogLevel.Error
                    );
                    return false;
                }

                return true;
            });
        }

        /// <summary>Upload a local file to the remote path.</summary>
        public async Task<bool> UploadFile(string remotePath, string localPath)
        {
            byte[] data = await File.ReadAllBytesAsync(localPath);
            return await this.Upload(remotePath, data);
        }

        /// <summary>Download a file from the remote path.</summary>
        public async Task<byte[]?> Download(string remotePath)
        {
            return await this.WithRetry(async () =>
            {
                string url = this.BuildUrl(remotePath);
                var response = await this.Http.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    this.Monitor.Log(
                        $"File not found on remote: {remotePath}",
                        LogLevel.Debug
                    );
                    return (byte[]?)null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    this.Monitor.Log(
                        $"Download failed: {response.StatusCode} for {remotePath}",
                        LogLevel.Error
                    );
                    return null;
                }

                return await response.Content.ReadAsByteArrayAsync();
            });
        }

        /// <summary>Download a remote file to a local path.</summary>
        public async Task<bool> DownloadFile(string remotePath, string localPath)
        {
            byte[]? data = await this.Download(remotePath);
            if (data == null)
                return false;

            string? dir = Path.GetDirectoryName(localPath);
            if (dir != null)
                Directory.CreateDirectory(dir);

            await File.WriteAllBytesAsync(localPath, data);
            return true;
        }

        /// <summary>Create a directory (and parents) on the remote.</summary>
        public async Task<bool> CreateDirectory(string remotePath)
        {
            string[] segments = remotePath.Trim('/').Split('/');
            string current = "";

            foreach (string segment in segments)
            {
                current += "/" + segment;
                string url = this.BuildUrl(current);

                var request = new HttpRequestMessage(new HttpMethod("MKCOL"), url);
                var response = await this.Http.SendAsync(request);

                if (!response.IsSuccessStatusCode &&
                    response.StatusCode != HttpStatusCode.MethodNotAllowed)
                {
                    this.Monitor.Log(
                        $"MKCOL failed: {response.StatusCode} for {current}",
                        LogLevel.Error
                    );
                    return false;
                }
            }

            return true;
        }

        /// <summary>Get metadata for files in a remote directory.</summary>
        public async Task<List<WebDavResource>?> PropFind(string remotePath)
        {
            return await this.WithRetry(async () =>
            {
                string url = this.BuildUrl(remotePath);
                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
                request.Headers.Add("Depth", "1");
                request.Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<d:propfind xmlns:d=\"DAV:\">" +
                    "<d:prop><d:getlastmodified/><d:getcontentlength/>" +
                    "<d:resourcetype/></d:prop></d:propfind>",
                    Encoding.UTF8, "application/xml"
                );

                var response = await this.Http.SendAsync(request);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return (List<WebDavResource>?)null;

                if (!response.IsSuccessStatusCode)
                {
                    this.Monitor.Log(
                        $"PROPFIND failed: {response.StatusCode} for {remotePath}",
                        LogLevel.Error
                    );
                    return null;
                }

                string xml = await response.Content.ReadAsStringAsync();
                return ParsePropFindResponse(xml);
            });
        }

        /// <summary>Delete a remote file or directory.</summary>
        public async Task<bool> Delete(string remotePath)
        {
            string url = this.BuildUrl(remotePath);
            var response = await this.Http.DeleteAsync(url);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return true;

            if (!response.IsSuccessStatusCode)
            {
                this.Monitor.Log(
                    $"DELETE failed: {response.StatusCode} for {remotePath}",
                    LogLevel.Error
                );
                return false;
            }

            return true;
        }

        /// <summary>Get last-modified time for a single remote file.</summary>
        public async Task<DateTime?> GetLastModified(string remotePath)
        {
            var resources = await this.PropFind(remotePath);
            if (resources == null || resources.Count == 0)
                return null;

            return resources[0].LastModified;
        }

        private static List<WebDavResource> ParsePropFindResponse(string xml)
        {
            var result = new List<WebDavResource>();
            XNamespace d = "DAV:";

            var doc = XDocument.Parse(xml);
            foreach (var response in doc.Descendants(d + "response"))
            {
                var resource = new WebDavResource
                {
                    Href = response.Element(d + "href")?.Value ?? ""
                };

                var propStat = response.Element(d + "propstat");
                var prop = propStat?.Element(d + "prop");

                if (prop != null)
                {
                    string? lastMod = prop.Element(d + "getlastmodified")?.Value;
                    if (!string.IsNullOrEmpty(lastMod) &&
                        DateTime.TryParse(lastMod, out var dt))
                    {
                        resource.LastModified = dt.ToUniversalTime();
                    }

                    string? length = prop.Element(d + "getcontentlength")?.Value;
                    if (long.TryParse(length, out long len))
                        resource.ContentLength = len;

                    resource.IsCollection =
                        prop.Element(d + "resourcetype")?
                            .Element(d + "collection") != null;
                }

                result.Add(resource);
            }

            return result;
        }

        private async Task<T> WithRetry<T>(Func<Task<T>> action)
        {
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await action();
                }
                catch (TaskCanceledException) when (attempt < MaxRetries)
                {
                    this.Monitor.Log("Request timed out, retrying...", LogLevel.Warn);
                }
                catch (HttpRequestException ex) when (attempt < MaxRetries)
                {
                    this.Monitor.Log(
                        $"Network error: {ex.Message}, retrying...",
                        LogLevel.Warn
                    );
                }
            }

            return await action();
        }

    }
}
