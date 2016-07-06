﻿using Mirai.Database.Tables;
using Mirai.Handlers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.InputMessageContents;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramMessage = Telegram.Bot.Types.Message;

namespace Mirai.Client
{
    class Telegram : IClient
    {
        private TelegramBotClient Client;
        private ConcurrentDictionary<SendMessage, TelegramMessage> Sent = new ConcurrentDictionary<SendMessage, TelegramMessage>();
        private static ConcurrentDictionary<long, string> InlineIds = new ConcurrentDictionary<long, string>();
        private string Token;
        private string Mention;
        public string Owner { get; set; }

        public bool Connected
        {
            get
            {
                return Client.TestApiAsync().Result;
            }
        }

        public Telegram(string Token)
        {
            this.Token = Token;
        }

        public async Task Connect()
        {
            Client = new TelegramBotClient(Token);
            await UpdateCache();

            Client.OnMessage += OnMessage;
            Client.OnInlineQuery += OnInlineQuery;
            Client.OnInlineResultChosen += OnInlineResultChosen;
            Client.StartReceiving();
        }

        public async Task Disconnect()
        {
            Client.StopReceiving();
        }

        public async Task Send(SendMessage Message)
        {
            var Text = Message.Text.Replace("**", "*").Replace("__", "_");
            Message.Text = null;

            try
            {
                if (Message.State != null)
                {
                    var ResultId = (int)Message.State;
                    string InlineId;

                    for (int i = 0; i < 10; i++)
                    {
                        if (InlineIds.TryRemove(ResultId, out InlineId))
                        {
                            try
                            {
                                //await Client.EditInlineMessageCaptionAsync(InlineId, "Response");
                                await Client.EditInlineMessageTextAsync(InlineId, Text, ParseMode.Markdown);
                                //await Client.EditInlineMessageReplyMarkupAsync(InlineId, null);
                            }
                            catch (Exception Ex)
                            {
                                Bot.Log(Ex);
                            }

                            return;
                        }

                        await Task.Delay(100);
                    }

                    Bot.Log("Could not find InlineId");
                }

                if (!Sent.TryAdd(Message, await Client.SendTextMessageAsync(long.Parse(Message.Chat), Text, parseMode: ParseMode.Markdown)))
                {
                    Bot.Log("Failed to add message to the telegram sent list");
                }
            }
            catch (ApiRequestException Ex)
            {
                Bot.Log(Ex);
            }
        }

        public async Task Edit(SendMessage Message)
        {
            var Text = Message.Text.Replace("**", "*").Replace("__", "_");
            Message.Text = null;

            TelegramMessage MessageObj;
            if (Sent.TryGetValue(Message, out MessageObj))
            {
                try
                {
                    await Client.EditMessageTextAsync(MessageObj.Chat.Id.ToString(), MessageObj.MessageId, Text, parseMode: ParseMode.Markdown);
                }
                catch (ApiRequestException Ex)
                {
                    Bot.Log(Ex);
                }
            }
        }

        public async Task Delete(SendMessage Message)
        {
            TelegramMessage MessageObj;
            Sent.TryRemove(Message, out MessageObj);
        }

        public async Task Stream(string Chat, byte[] Sound)
        {
        }

        public async Task<ClientInformation> Info()
        {
            var UserData = await Client.GetMeAsync();
            return new ClientInformation()
            {
                Id = UserData.Id.ToString(),
                Name = UserData.Username,
                Type = typeof(Telegram)
            };
        }

        public async Task UpdateCache()
        {
            Mention = $"@{(await Info()).Name}";
            //Add Feedlink Updates
        }

        private async void OnMessage(object sender, MessageEventArgs e)
        {
            TelegramFeedlink FeedLink;

            //Remove this query
            using (var Context = Bot.GetDb)
            {
                FeedLink = Context.TelegramFeedlink.Where(x => x.Token == Token && x.Chat == e.Message.Chat.Id).FirstOrDefault();
            }

            var Text = e.Message.Text;
            if (Text == null)
            {
                return;
            }

            byte JoinFeedId;
            if (FeedLink != null)
            {
                if (e.Message.From.Id.ToString() == Owner && Text == Bot.Command + Bot.LeaveFeed + Mention)
                {
                    using (var Context = Bot.GetDb)
                    {
                        Context.TelegramFeedlink.Attach(FeedLink);
                        Context.TelegramFeedlink.Remove(FeedLink);
                        await Context.SaveChangesAsync();
                    }

                    Bot.UpdateCache();
                    Bot.Log($"Removed feed from {e.Message.Chat.Title} on {Mention}");
                    return;
                }

                var Message = new ReceivedMessage
                {
                    Origin = new Destination
                    {
                        Token = Token,
                        Chat = e.Message.Chat.Id.ToString()
                    },
                    Id = e.Message.MessageId,
                    Sender = e.Message.From.Id.ToString(),
                    SenderMention = $"@{e.Message.From.Username}",
                    Text = e.Message.Text,
                    Mentions = e.Message.Entities.Where(x => x.User != null).Select(x => new ReceivedMessageMention
                    {
                        Id = x.User.Id.ToString(),
                        Mention = $"@{x.User.Username}"
                    }).ToArray(),
                    State = null
                };

                var Trimmed = string.Empty;
                if (Message.Text.StartsWith(Bot.Command))
                {
                    Trimmed = Message.Text.Substring(Bot.Command);
                }

                if (Trimmed != string.Empty)
                {
                    Message.Command = Trimmed.Split(' ')[0];
                    if (Message.Command.Contains("@") && !Message.Command.EndsWith(Mention))
                    {
                        //Another bot was requested
                        Message.Command = null;
                    }
                    else
                    {
                        Message.Text = Trimmed.Substring(Message.Command).TrimStart();
                        Message.Command = Message.Command.Replace(Mention, "");

                        int InlineId;
                        var PossibleId = Message.Text.Split(' ')[0];
                        if (PossibleId.StartsWith("[") && PossibleId.EndsWith("]") && int.TryParse(PossibleId.Substring(1, PossibleId.Length - 2), out InlineId))
                        {
                            Message.Text = Message.Text.Substring(PossibleId).TrimStart();
                            Message.State = InlineId;
                        }
                    }
                }

                Bot.Feeds[FeedLink.Feed].Handle(Message);
            }
            else if (e.Message.From.Id.ToString() == Owner)
            {
                var JoinFeed = Bot.Command + Bot.JoinFeed + Mention + " ";

                if (Text.StartsWith(JoinFeed) && byte.TryParse(Text.Substring(JoinFeed), out JoinFeedId) && JoinFeedId < Bot.Feeds.Length)
                {
                    using (var Context = Bot.GetDb)
                    {
                        Context.TelegramFeedlink.Add(new TelegramFeedlink()
                        {
                            Token = Token,
                            Chat = e.Message.Chat.Id,
                            Feed = JoinFeedId
                        });

                        await Context.SaveChangesAsync();
                    }

                    Bot.UpdateCache();
                    Bot.Log($"Added feed to {e.Message.Chat.Title} on {Mention}");
                }
            }
        }

        private static long Identifier = 0;
        private async void OnInlineQuery(object sender, InlineQueryEventArgs e)
        {
            try
            {
                long MsgId = Interlocked.Increment(ref Identifier) * MusicHandler.MaxQueued;

                var Results = new List<InlineQueryResult>();
                InlineQueryResultVideo Result;

                int i = 0;

                if (e.InlineQuery.Query == "remove")
                {
                    while (i < MusicHandler.MaxQueued)
                    {
                        Result = Result = new InlineQueryResultVideo();
                        Result.Id = (MsgId + i++).ToString();
                        Result.Title = "Remove Song";
                        Result.Description = $"#{i}";
                        Result.ThumbUrl = "https://github.com/google/material-design-icons/blob/master/av/2x_web/ic_note_black_48dp.png?raw=true";
                        Result.MimeType = "text/html";
                        Result.Url = Result.ThumbUrl;
                        Result.InputMessageContent = new InputTextMessageContent();
                        ((InputTextMessageContent)Result.InputMessageContent).MessageText = $"/remove{Mention} [{MsgId}] {i}";
                        Result.ReplyMarkup = IKM("Loading..", "http://www.google.nl", "/");

                        Results.Add(Result);
                    }
                }
                else
                {
                    foreach (var Key in new string[] { "skip", "repeat" })
                    {
                        if (Key.StartsWith(e.InlineQuery.Query))
                        {
                            Result = Result = new InlineQueryResultVideo();
                            Result.Id = (MsgId + i++).ToString();
                            Result.Title = "Use command";
                            Result.Description = Key;
                            Result.ThumbUrl = "https://github.com/google/material-design-icons/blob/master/av/2x_web/ic_note_black_48dp.png?raw=true";
                            Result.MimeType = "text/html";
                            Result.Url = Result.ThumbUrl;
                            Result.InputMessageContent = new InputTextMessageContent();
                            ((InputTextMessageContent)Result.InputMessageContent).MessageText = $"/{Key}{Mention} [{MsgId}]";
                            Result.ReplyMarkup = IKM("Loading..", "http://www.google.com", "/");

                            Results.Add(Result);
                        }
                    }

                    var Files = SongData.LocalFiles(e.InlineQuery.Query);

                    var ListRequest = SongData.YT.Search.List("snippet");
                    ListRequest.MaxResults = MusicHandler.MaxQueued - i;

                    if (Files.Length < ListRequest.MaxResults)
                    {
                        ListRequest.MaxResults -= Files.Length;

                        foreach (var File in Files)
                        {
                            Result = new InlineQueryResultVideo();
                            Result.Id = (MsgId + i++).ToString();
                            Result.Title = File;
                            Result.Title = Result.Title;
                            Result.Caption = Result.Title;
                            Result.Description = "Music File";
                            Result.ThumbUrl = "https://github.com/google/material-design-icons/blob/master/av/2x_web/ic_play_arrow_black_48dp.png?raw=true";
                            Result.MimeType = "video/mp4";
                            Result.Url = Result.ThumbUrl;
                            Result.InputMessageContent = new InputTextMessageContent();
                            ((InputTextMessageContent)Result.InputMessageContent).MessageText = $"/add{Mention} [{MsgId}] {Result.Title}";
                            Result.ReplyMarkup = IKM("Loading..", Result.ThumbUrl, "/");
                            Results.Add(Result);
                        }
                    }

                    if (e.InlineQuery.Query.IsValidUrl() && ListRequest.MaxResults != 0)
                    {
                        ListRequest.MaxResults--;

                        Result = new InlineQueryResultVideo();
                        Result.Id = (MsgId + i++).ToString();
                        Result.Title = e.InlineQuery.Query;
                        Result.Caption = Result.Title;
                        Result.Description = "Remote File";
                        Result.ThumbUrl = "https://github.com/google/material-design-icons/blob/master/av/2x_web/ic_play_arrow_black_48dp.png?raw=true";
                        Result.MimeType = "video/mp4";
                        Result.Url = Result.ThumbUrl;
                        Result.InputMessageContent = new InputTextMessageContent();
                        ((InputTextMessageContent)Result.InputMessageContent).MessageText = $"/add{Mention} [{MsgId}] {e.InlineQuery.Query}";
                        Result.ReplyMarkup = IKM("Loading..", Result.ThumbUrl, "/");
                        Results.Add(Result);
                    }

                    if (ListRequest.MaxResults != 0)
                    {
                        ListRequest.Q = e.InlineQuery.Query;
                        ListRequest.Type = "video";

                        foreach (var Video in (await ListRequest.ExecuteAsync()).Items)
                        {
                            Result = new InlineQueryResultVideo();
                            Result.Id = (MsgId + i++).ToString();
                            Result.Title = Video.Snippet.Title;
                            Result.Caption = Result.Title;
                            Result.Description = Video.Snippet.Description;
                            Result.ThumbUrl = Video.Snippet.Thumbnails.Maxres?.Url ?? Video.Snippet.Thumbnails.Default__?.Url ?? string.Empty;
                            Result.MimeType = "text/html";
                            Result.Url = $"https://youtu.be/{Video.Id.VideoId}";
                            Result.InputMessageContent = new InputTextMessageContent();
                            ((InputTextMessageContent)Result.InputMessageContent).MessageText = $"/add{Mention} [{MsgId}] {Result.Url}";
                            Result.ReplyMarkup = IKM("Open Song", Result.Url, "/");
                            Results.Add(Result);
                        }
                    }
                }

                await Client.AnswerInlineQueryAsync(e.InlineQuery.Id, Results.ToArray(), 0);
            }
            catch (Exception Ex)
            {
                Bot.Log(Ex);
            }
        }

        private static InlineKeyboardMarkup IKM(string Text, string Url, string CallbackData)
        {
            var Markup = new InlineKeyboardMarkup();
            Markup.InlineKeyboard = new InlineKeyboardButton[][] { new InlineKeyboardButton[] { new InlineKeyboardButton() } };
            Markup.InlineKeyboard[0][0].Text = Text;
            Markup.InlineKeyboard[0][0].Url = Url;
            Markup.InlineKeyboard[0][0].CallbackData = CallbackData;
            return Markup;
        }

        private async void OnInlineResultChosen(object sender, ChosenInlineResultEventArgs e)
        {
            try
            {
                long Result;
                if (long.TryParse(e.ChosenInlineResult.ResultId, out Result))
                {
                    Result -= (Result % MusicHandler.MaxQueued);
                    if (InlineIds.TryAdd(Result, e.ChosenInlineResult.InlineMessageId))
                    {
                        await Task.Delay(3000);
                        string Value;
                        if (InlineIds.TryRemove(Result, out Value))
                        {
                            Bot.Log("The inline message id was not used");
                        }
                    }
                }
                else
                {
                    Bot.Log("Could not add the inline message id");
                }
            }
            catch (Exception Ex)
            {
                Bot.Log(Ex);
            }
        }
    }
}
