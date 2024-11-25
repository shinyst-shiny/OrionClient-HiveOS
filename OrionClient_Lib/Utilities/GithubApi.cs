using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Utilities
{
    public class GithubApi
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private static HttpClient _client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com"),
            Timeout = TimeSpan.FromSeconds(3)
        };

        static GithubApi()
        {
            _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0");
        }

        public static async Task<Data> CheckForUpdates(string version)
        {
            if(!Version.TryParse(version, out Version v))
            {
                return null;
            }

            try
            {
                string response = await _client.GetStringAsync("/repos/sl-x-tnt/OrionClient/releases");


                List<Data> releases = JsonConvert.DeserializeObject<List<Data>>(response);

                if(releases.Count > 0)
                {
                    var latestRelease = releases.OrderByDescending(x => x.CreatedAt).FirstOrDefault();

                    if(Version.TryParse(latestRelease.TagName.Replace("v", ""), out Version newVersion) && newVersion > v)
                    {
                        return latestRelease;
                    }
                }
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Failed to check for updates");
            }

            return null;
        }


        public class Data
        {
            [JsonProperty("url")]
            public string Url { get; set; }

            [JsonProperty("assets_url")]
            public string AssetsUrl { get; set; }

            [JsonProperty("upload_url")]
            public string UploadUrl { get; set; }

            [JsonProperty("html_url")]
            public string HtmlUrl { get; set; }

            [JsonProperty("id")]
            public int Id { get; set; }

            [JsonProperty("node_id")]
            public string NodeId { get; set; }

            [JsonProperty("tag_name")]
            public string TagName { get; set; }

            [JsonProperty("target_commitish")]
            public string TargetCommitish { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("draft")]
            public bool Draft { get; set; }

            [JsonProperty("prerelease")]
            public bool Prerelease { get; set; }

            [JsonProperty("created_at")]
            public DateTime CreatedAt { get; set; }

            [JsonProperty("published_at")]
            public DateTime PublishedAt { get; set; }

            [JsonProperty("tarball_url")]
            public string TarballUrl { get; set; }

            [JsonProperty("zipball_url")]
            public string ZipballUrl { get; set; }

            [JsonProperty("body")]
            public string Body { get; set; }
        }

    }
}
