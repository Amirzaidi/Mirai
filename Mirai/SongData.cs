﻿using Google.Apis.YouTube.v3;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VideoLibrary;

namespace Mirai
{
    enum SongType
    {
        Local,
        Remote,
        YouTube,
        SoundCloud
    }
    
    struct SongData
    {
        internal static YouTubeService YT;
        internal static string MusicDir = Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%") + "\\Music\\";
        private static readonly Regex YoutubeVideoRegex = new Regex(@"youtu(?:\.be|be\.com)/(?:(.*)v(/|=)|(.*/)?)([a-zA-Z0-9-_]+)", RegexOptions.IgnoreCase);
        
        internal string FullName;
        internal string Url;
        internal SongType Type;
        internal string Thumbnail;

        internal string Title
        {
            get
            {
                if (FullName.Length < 100)
                {
                    return FullName;
                }

                return FullName.Substring(0, 100);
            }
        }

        internal string StreamUrl
        {
            get
            {
                if (Type == SongType.YouTube)
                {
                    var Videos = YouTube.Default.GetAllVideos(Url);

                    YouTubeVideo MaxVid = null;
                    foreach (var Vid in Videos)
                    {
                        if (Vid.AudioFormat == AudioFormat.Aac && (MaxVid == null || Vid.AudioBitrate >= MaxVid.AudioBitrate))
                        {
                            MaxVid = Vid;
                        }
                    }
                    
                    return MaxVid?.Uri ?? string.Empty;
                }
                else if (Type == SongType.SoundCloud)
                {
                    var SC = ($"http://api.soundcloud.com/resolve?url={Url}&client_id={Bot.Config["SoundCloud"]}").WebResponse().Result;
                    if (SC != string.Empty && SC.StartsWith("{\"kind\":\"track\""))
                    {
                        return $"{JObject.Parse(SC)["stream_url"]}?client_id={Bot.Config["SoundCloud"]}";
                    }
                }

                return Url;
            }
        }

        internal static List<SongData> Search(object ToSearch, int SoftLimit = 10)
        {
            var Query = ((string)ToSearch).Trim();
            var Results = new List<SongData>();

            if (Query.StartsWith(MusicDir))
            {
                Results.Add(new SongData
                {
                    FullName = Query.Substring(MusicDir.Length),
                    Url = Query,
                    Type = SongType.Local
                });

                return Results;
            }

            if (Query.Length >= 3)
            {
                Results.AddRange(new DirectoryInfo(MusicDir).GetFiles()
                    .Where(x => x.Name.Length >= Query.Length && x.Name.ToLower().Contains(Query.ToLower()) && !x.Attributes.HasFlag(FileAttributes.System))
                    .OrderBy(x => x.Name)
                    .Select(x => new SongData
                    {
                        FullName = x.Name,
                        Url = x.FullName,
                        Type = SongType.Local
                    }));
            }

            if (Query.IsValidUrl())
            {
                if (Regex.IsMatch(Query, @"http(s)?://(www\.)?(youtu\.be|youtube\.com)[\w-/=&?]+"))
                {
                    var Match = YoutubeVideoRegex.Match(Query);
                    if (Match.Success)
                    {
                        var Search = YT.Videos.List("snippet");
                        Search.Id = Match.Groups[4].Value;
                        var Videos = Search.Execute();
                        var Result = Videos.Items.First();
                        if (Result != null)
                        {
                            Results.Add(new SongData
                            {
                                FullName = Result.Snippet.Title,
                                Url = Query,
                                Type = SongType.YouTube,
                                Thumbnail = Result.Snippet.Thumbnails.Maxres?.Url ?? Result.Snippet.Thumbnails.Default__?.Url
                            });
                        }
                    }

                }
                else if (Regex.IsMatch(Query, "(.*)(soundcloud.com|snd.sc)(.*)"))
                {
                    try
                    {
                        var SC = ($"http://api.soundcloud.com/resolve?url={Query}&client_id={Bot.Config["SoundCloud"]}").WebResponse().Result;
                        if (SC != string.Empty && SC.StartsWith("{\"kind\":\"track\""))
                        {
                            var Response = JObject.Parse(SC);
                            Results.Add(new SongData
                            {
                                FullName = Response["title"].ToString(),
                                Url = Query,
                                Type = SongType.SoundCloud
                            });
                        }
                    }
                    catch (Exception Ex)
                    {
                        Console.WriteLine(Ex.ToString());
                    }
                }
                else
                {
                    Results.Add(new SongData
                    {
                        FullName = Query,
                        Url = Query,
                        Type = SongType.Remote
                    });
                }
            }

            if (Results.Count < SoftLimit)
            {
                var ListRequest = YT.Search.List("snippet");
                ListRequest.Q = Query;
                ListRequest.MaxResults = SoftLimit - Results.Count;
                ListRequest.Type = "video";
                foreach (var Result in ListRequest.Execute().Items)
                {
                    Results.Add(new SongData
                    {
                        FullName = Result.Snippet.Title,
                        Url = $"http://www.youtube.com/watch?v={Result.Id.VideoId}",
                        Type = SongType.YouTube,
                        Thumbnail = Result.Snippet.Thumbnails.Maxres?.Url ?? Result.Snippet.Thumbnails.Default__?.Url
                    });
                }
            }

            return Results;
        }

        internal static List<SongData> Playlist(string List, ref string Name)
        {
            var Vids = new List<SongData>();
            
            var S1 = YT.Search.List("snippet");
            S1.Q = List;
            var Response = S1.Execute();
            if (Response.Items.Count != 0)
            {
                Name = Response.Items[0].Snippet.Title;

                var S = YT.PlaylistItems.List("snippet,contentDetails");
                S.PlaylistId = Response.Items[0].Id.PlaylistId;
                S.MaxResults = 50;

                do
                {
                    var E = S.Execute();
                    foreach (var Result in E.Items)
                    {
                        Vids.Add(new SongData
                        {
                            FullName = Result.Snippet.Title,
                            Url = $"http://www.youtube.com/watch?v={Result.ContentDetails.VideoId}",
                            Type = SongType.YouTube,
                            Thumbnail = Result.Snippet.Thumbnails.Maxres?.Url ?? Result.Snippet.Thumbnails.Default__?.Url
                        });
                    }

                    S.PageToken = E.NextPageToken;
                }
                while (S.PageToken != null);
            }

            return Vids;
        }
    }
}
