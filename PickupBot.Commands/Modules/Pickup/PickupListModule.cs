﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using PickupBot.Commands.Extensions;
using PickupBot.Commands.Infrastructure.Helpers;
using PickupBot.Commands.Infrastructure.Services;
using PickupBot.Commands.Infrastructure.Utilities;
using PickupBot.Data.Models;
using PickupBot.Data.Repositories;

namespace PickupBot.Commands.Modules
{
    [Name("Pickup list actions")]
    [Summary("Commands for handling pickup list actions")]
    public class PickupListModule : ModuleBase<SocketCommandContext>, IDisposable
    {
        private readonly IQueueRepository _queueRepository;
        private readonly ISubscriberActivitiesRepository _activitiesRepository;
        private readonly IListCommandService _listCommandService;
        private readonly ISubscriberCommandService _subscriberCommandService;
        private readonly IMiscCommandService _miscCommandService;
        private readonly DiscordSocketClient _client;

        public PickupListModule(
            IQueueRepository queueRepository,
            ISubscriberActivitiesRepository activitiesRepository,
            IListCommandService listCommandService,
            ISubscriberCommandService subscriberCommandService,
            IMiscCommandService miscCommandService,
            DiscordSocketClient client)
        {
            _queueRepository = queueRepository;
            _activitiesRepository = activitiesRepository;
            _listCommandService = listCommandService;
            _subscriberCommandService = subscriberCommandService;
            _miscCommandService = miscCommandService;
            _client = client;

            _client.ReactionAdded += ReactionAdded;
            _client.ReactionRemoved += ReactionRemoved;
        }

        private async Task ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            if (!(channel is IGuildChannel guildChannel) || guildChannel.Name != "active-pickups") return;
            if (reaction.User.Value.IsBot) return;

            var queue = await _queueRepository.FindQueueByMessageId(reaction.MessageId, guildChannel.GuildId.ToString()).ConfigureAwait(false);
            if (queue == null) return;

            var pickupChannel = ((SocketGuild)guildChannel.Guild).Channels.FirstOrDefault(c => c.Name.Equals("pickup")) as SocketTextChannel;
            switch (reaction.Emote.Name)
            {
                case "\u2705":
                    await _subscriberCommandService.Add(queue.Name,
                        pickupChannel ?? (SocketTextChannel)guildChannel,
                        (SocketGuildUser)reaction.User).ConfigureAwait(false);
                    break;
                case "\uD83D\uDCE2":
                    await _listCommandService.Promote(
                        queue,
                        pickupChannel ?? (SocketTextChannel)guildChannel,
                        (SocketGuildUser)reaction.User).ConfigureAwait(false);
                    break;
            }
        }

        private async Task ReactionRemoved(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            if (!(channel is IGuildChannel guildChannel) || guildChannel.Name != "active-pickups") return;
            if (reaction.User.Value.IsBot) return;

            if (reaction.Emote.Name == "\u2705")
            {
                var queue = await _queueRepository.FindQueueByMessageId(reaction.MessageId, guildChannel.GuildId.ToString()).ConfigureAwait(false);

                if (queue != null)
                {
                    var pickupChannel = ((SocketGuild)guildChannel.Guild).Channels.FirstOrDefault(c => c.Name.Equals("pickup")) as SocketTextChannel;
                    await _subscriberCommandService.Leave(queue, pickupChannel ?? (SocketTextChannel)guildChannel,
                        (SocketGuildUser)reaction.User).ConfigureAwait(false);
                }
            }
        }

        [Command("create")]
        [Summary("Creates a pickup queue")]
        public async Task Create(
            [Name("Queue name")] string queueName,
            [Name("Team size")]
            int? teamSize = null,
            [Remainder, Name("Operator flags")] string operators = "")
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel))
                return;

            if (!teamSize.HasValue)
                teamSize = 4;

            if (teamSize > 16)
                teamSize = 16;

            if (!await _miscCommandService.VerifyUserFlaggedStatus((IGuildUser)Context.User, Context.Channel).ConfigureAwait(false))
                return;

            var ops = OperatorParser.Parse(operators);

            //find queue with name {queueName}
            var queue = await _queueRepository.FindQueue(queueName, Context.Guild.Id.ToString()).ConfigureAwait(false);

            if (queue != null)
            {
                await Context.Channel.SendMessageAsync($"`Queue with the name '{queueName}' already exists!`").AutoRemoveMessage(10).ConfigureAwait(false);
                return;
            }

            var activity = await _activitiesRepository.Find((IGuildUser)Context.User).ConfigureAwait(false);
            activity.PickupCreate += 1;
            activity.PickupAdd += 1;
            await _activitiesRepository.Update(activity).ConfigureAwait(false);

            var rconEnabled = ops?.ContainsKey("-rcon") ?? true;
            if (ops?.ContainsKey("-norcon") == true)
                rconEnabled = false;

            queue = new PickupQueue(Context.Guild.Id.ToString(), queueName)
            {
                Name = queueName,
                GuildId = Context.Guild.Id.ToString(),
                OwnerName = PickupHelpers.GetNickname(Context.User),
                OwnerId = Context.User.Id.ToString(),
                Created = DateTime.UtcNow,
                Updated = DateTime.UtcNow,
                TeamSize = teamSize.Value,
                IsCoop = ops?.ContainsKey("-coop") ?? false,
                Rcon = rconEnabled,
                Subscribers = new List<Subscriber>
                    {new Subscriber {Id = Context.User.Id, Name = PickupHelpers.GetNickname(Context.User)}},
                Host = ops?.ContainsKey("-host") ?? false ? ops["-host"]?.FirstOrDefault() : null,
                Port = int.Parse((ops?.ContainsKey("-port") ?? false ? ops["-port"]?.FirstOrDefault() : null) ?? "0"),
                Games = ops?.ContainsKey("-game") ?? false ? ops["-game"] : Enumerable.Empty<string>(),
            };

            await _queueRepository.AddQueue(queue).ConfigureAwait(false);

            await Context.Channel.SendMessageAsync($"`Queue '{queueName}' was added by {PickupHelpers.GetNickname(Context.User)}`").ConfigureAwait(false);
            queue = await _listCommandService.SaveStaticQueueMessage(queue, Context.Guild).ConfigureAwait(false);
            await _queueRepository.UpdateQueue(queue).ConfigureAwait(false);
        }

        [Command("rename")]
        [Summary("Rename a queue")]
        public async Task Rename([Name("Queue name")] string queueName, [Name("New name")] string newName)
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel)) return;

            var queue = await _miscCommandService.VerifyQueueByName(queueName, (IGuildChannel)Context.Channel).ConfigureAwait(false);
            if (queue == null) return;

            var isAdmin = (Context.User as IGuildUser)?.GuildPermissions.Has(GuildPermission.Administrator) ?? false;
            if (isAdmin || queue.OwnerId == Context.User.Id.ToString())
            {
                var newQueueCheck = await _queueRepository.FindQueue(newName, Context.Guild.Id.ToString()).ConfigureAwait(false);
                if (newQueueCheck != null)
                {
                    await ReplyAsync($"`A queue with the name '{newName}' already exists.`").AutoRemoveMessage(10).ConfigureAwait(false);
                    return;
                }

                var newQueue = (PickupQueue)queue.Clone();
                newQueue.RowKey = newName.ToLowerInvariant();
                newQueue.Name = newName;

                var result = await _queueRepository.AddQueue(newQueue);
                if (result)
                {
                    await _queueRepository.RemoveQueue(queue).ConfigureAwait(false);
                    await ReplyAsync($"The queue '{queue.Name}' has been renamed to '{newQueue.Name}'");
                    await ReplyAsync($"`{newQueue.Name} - {PickupHelpers.ParseSubscribers(newQueue)}`");
                    if (!string.IsNullOrEmpty(queue.StaticMessageId))
                        await _listCommandService.SaveStaticQueueMessage(newQueue, Context.Guild).ConfigureAwait(false);
                    return;
                }

                await ReplyAsync("An error occured when trying to update the queue name, try again.")
                    .AutoRemoveMessage(10)
                    .ConfigureAwait(false);
            }
            else
            {
                await ReplyAsync("`You do not have permission to rename this queue, you have to be either the owner or a server admin`")
                    .AutoRemoveMessage(10)
                    .ConfigureAwait(false);
            }
        }

        [Command("delete")]
        [Alias("del", "cancel")]
        [Summary("If you are the creator of the queue you can use this to delete it")]
        public async Task Delete([Name("Queue name"), Summary("Queue name"), Remainder] string queueName)
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel))
                return;

            var queue = await _miscCommandService.VerifyQueueByName(queueName, (IGuildChannel)Context.Channel).ConfigureAwait(false);
            if (queue == null)
            {
                return;
            }

            var isAdmin = (Context.User as IGuildUser)?.GuildPermissions.Has(GuildPermission.Administrator) ?? false;
            if (isAdmin || queue.OwnerId == Context.User.Id.ToString())
            {
                var queuesChannel = await PickupHelpers.GetPickupQueuesChannel(Context.Guild).ConfigureAwait(false);

                var result = await _queueRepository.RemoveQueue(queueName, Context.Guild.Id.ToString()).ConfigureAwait(false);
                var message = result ?
                    $"`Queue '{queueName}' has been canceled`" :
                    $"`Queue with the name '{queueName}' doesn't exists or you are not the owner of the queue!`";
                await Context.Channel.SendMessageAsync(message).AutoRemoveMessage(10).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(queue.StaticMessageId))
                    await queuesChannel.DeleteMessageAsync(Convert.ToUInt64(queue.StaticMessageId)).ConfigureAwait(false);

                return;
            }

            await Context.Channel.SendMessageAsync("You do not have permission to remove the queue.").AutoRemoveMessage(10).ConfigureAwait(false);
        }

        [Command("list")]
        [Summary("List all active queues")]
        public async Task List()
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel))
                return;

            //find all active queues
            var queues = await _queueRepository.AllQueues(Context.Guild.Id.ToString()).ConfigureAwait(false);
            Embed embed;
            //if queues found
            var pickupQueues = queues as PickupQueue[] ?? queues.ToArray();
            if (!pickupQueues.Any())
            {
                embed = new EmbedBuilder
                {
                    Title = "Active queues",
                    Description = "There are no active pickup queues at this time, maybe you should `!create` one \uD83D\uDE09",
                    Color = Color.Orange
                }.Build();

                await Context.Channel.SendMessageAsync(embed: embed).AutoRemoveMessage(10).ConfigureAwait(false);
                return;
            }

            var ordered = pickupQueues.OrderByDescending(w => w.Readiness);
            foreach (var q in ordered)
            {
                embed = new EmbedBuilder
                {
                    Title = $"{q.Name}{(q.Started ? " - Started" : "")}",
                    Description = BuildListResponse(q).ToString(),
                    Color = Color.Orange
                }.Build();
                await Context.Channel.SendMessageAsync(embed: embed).AutoRemoveMessage().ConfigureAwait(false);
            }
        }

        private static StringBuilder BuildListResponse(PickupQueue queue)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"`!add \"{queue.Name}\"` to join!")
                .AppendLine("")
                .AppendLine($"Created by _{queue.OwnerName}_ {(queue.IsCoop ? "(_coop_)" : "")}")
                .AppendLine("```")
                .AppendLine($"[{queue.Subscribers.Count}/{queue.MaxInQueue}] - {PickupHelpers.ParseSubscribers(q)}")
                .AppendLine("```");

            if (!queue.WaitingList.IsNullOrEmpty())
                sb.AppendLine($"In waitlist: **{queue.WaitingList.Count}**");
            if (!queue.Games.IsNullOrEmpty())
                sb.AppendLine($"**Game(s): ** _{string.Join(", ", queue.Games)}_");
            if (!string.IsNullOrWhiteSpace(queue.Host))
                sb.AppendLine($"**Server**: _{queue.Host ?? "ra3.se"}:{(queue.Port > 0 ? queue.Port : 27960)}_");
            return sb;
        }

        [Command("waitlist")]
        [Summary("Lists all the players in a given queues wait list")]
        public async Task WaitList([Name("Queue name"), Summary("Queue name"), Remainder] string queueName)
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel))
                return;

            var queue = await _queueRepository.FindQueue(queueName, Context.Guild.Id.ToString());

            if (queue == null)
            {
                await Context.Channel.SendMessageAsync($"`Queue with the name '{queueName}' doesn't exists!`").AutoRemoveMessage(10).ConfigureAwait(false);
                return;
            }

            var waitlist = string.Join($"{Environment.NewLine} ", queue.WaitingList.Select((w, i) => $"{i + 1}: {w.Name}"));
            if (string.IsNullOrWhiteSpace(waitlist))
                waitlist = "No players in the waiting list";

            var embed = new EmbedBuilder
            {
                Title = $"Players in waiting list for queue {queue.Name}",
                Description = waitlist,
                Color = Color.Orange
            }.Build();
            await Context.Channel.SendMessageAsync(embed: embed).AutoRemoveMessage(15).ConfigureAwait(false);
        }

        [Command("promote")]
        [Summary("Promotes one specific or all queues to the 'promote-role' role")]
        public async Task Promote([Name("Queue name"), Summary("Queue name"), Remainder] string queueName = "")
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel))
                return;

            PickupQueue queue = null;
            if (!string.IsNullOrWhiteSpace(queueName))
            {
                queue = await _queueRepository.FindQueue(queueName, Context.Guild.Id.ToString());
                if (queue == null)
                {
                    await Context.Channel.SendMessageAsync($"`Queue with the name '{queueName}' doesn't exists!`").AutoRemoveMessage(10).ConfigureAwait(false);
                    return;
                }

                if (queue.MaxInQueue <= queue.Subscribers.Count)
                {
                    await ReplyAsync("Queue is full, why the spam?").AutoRemoveMessage(10);
                    return;
                }
            }

            await _listCommandService.Promote(queue, (ITextChannel)Context.Channel, (IGuildUser)Context.User).ConfigureAwait(false);
        }

        [Command("start")]
        [Summary("Triggers the start of the game by splitting teams and setting up voice channels")]
        public async Task Start([Name("Queue name"), Summary("Queue name"), Remainder] string queueName)
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel)) return;

            var queue = await _miscCommandService.VerifyQueueByName(queueName, (IGuildChannel)Context.Channel).ConfigureAwait(false);
            if (queue == null || queue.Started) return;

            var pickupCategory = (ICategoryChannel)Context.Guild.CategoryChannels.FirstOrDefault(c =>
                              c.Name.Equals("Pickup voice channels", StringComparison.OrdinalIgnoreCase))
                           ?? await Context.Guild.CreateCategoryChannelAsync("Pickup voice channels");

            var vcRedTeamName = $"{queue.Name} \uD83D\uDD34";
            var vcBlueTeamName = $"{queue.Name} \uD83D\uDD35";

            var vcRed = await PickupHelpers.GetOrCreateVoiceChannel(vcRedTeamName, pickupCategory.Id, Context.Guild).ConfigureAwait(false);

            var vcBlue = queue.IsCoop ? null : await PickupHelpers.GetOrCreateVoiceChannel(vcBlueTeamName, pickupCategory.Id, Context.Guild);

            var halfPoint = (int)Math.Ceiling(queue.Subscribers.Count / (double)2);

            var rnd = new Random();
            var users = queue.Subscribers.OrderBy(s => rnd.Next()).Select(u => Context.Guild.GetUser(Convert.ToUInt64(u.Id))).ToList();

            var redTeam = queue.IsCoop ? users : users.Take(halfPoint).ToList();
            var blueTeam = queue.IsCoop ? Enumerable.Empty<SocketGuildUser>() : users.Skip(halfPoint).ToList();

            var redTeamName = $"{(queue.IsCoop ? "Coop" : "Red")} Team \uD83D\uDD34";

            queue.Teams.Clear();
            queue.Teams.Add(new Team
            {
                Name = redTeamName,
                Subscribers = redTeam.Select(w => new Subscriber { Id = w.Id, Name = PickupHelpers.GetNickname(w) }).ToList(),
                VoiceChannel = new KeyValuePair<string, ulong?>(vcRedTeamName, vcRed.Id)
            });

            if (!queue.IsCoop)
            {
                const string blueTeamName = "Blue Team \uD83D\uDD35";
                queue.Teams.Add(new Team
                {
                    Name = blueTeamName,
                    Subscribers = blueTeam.Select(w => new Subscriber { Id = w.Id, Name = PickupHelpers.GetNickname(w) }).ToList(),
                    VoiceChannel = new KeyValuePair<string, ulong?>(vcBlueTeamName, vcBlue?.Id)

                });
            }

            queue.Started = true;
            queue.Updated = DateTime.UtcNow;
            queue = await _listCommandService.SaveStaticQueueMessage(queue, Context.Guild).ConfigureAwait(false);
            await _queueRepository.UpdateQueue(queue).ConfigureAwait(false);
            await _listCommandService.PrintTeams(queue, Context.Channel, Context.Guild).ConfigureAwait(false);

            _miscCommandService.TriggerDelayedRconNotification(queue);
        }

        [Command("teams"), Alias("team")]
        [Summary("Lists the teams of a started pickup queue")]
        public async Task Teams([Name("Queue name"), Remainder] string queueName)
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel))
                return;

            var queue = await _miscCommandService.VerifyQueueByName(queueName, (IGuildChannel)Context.Channel).ConfigureAwait(false);
            if (queue == null) return;

            await _listCommandService.PrintTeams(queue, Context.Channel, Context.Guild).ConfigureAwait(false);

            await _miscCommandService.TriggerRconNotification(queue).ConfigureAwait(false);
        }

        [Command("stop")]
        [Summary("Triggers the end of the game by removing voice channels and removing the queue")]
        public async Task Stop([Name("Queue name"), Summary("Queue name")]string queueName)
        {
            if (!PickupHelpers.IsInPickupChannel((IGuildChannel)Context.Channel))
                return;

            var queue = await _miscCommandService.VerifyQueueByName(queueName, (IGuildChannel)Context.Channel).ConfigureAwait(false);
            if (queue == null) return;

            var voiceIds = queue.Teams.Select(w => w.VoiceChannel.Value).ToList();
            if (voiceIds.Any())
            {
                foreach (var voiceId in voiceIds)
                {
                    if (!voiceId.HasValue) continue;

                    var vc = (IVoiceChannel)Context.Guild.GetVoiceChannel(voiceId.Value);
                    if (vc == null) continue;
                    await vc.DeleteAsync().ConfigureAwait(false);
                }
            }

            queue.Started = false;
            queue.Updated = DateTime.UtcNow;
            queue.Teams.Clear();
            queue = await _listCommandService.SaveStaticQueueMessage(queue, Context.Guild).ConfigureAwait(false);
            await _queueRepository.UpdateQueue(queue).ConfigureAwait(false);

            await Delete(queueName).ConfigureAwait(false);
        }

        public void Dispose()
        {
            //remove event handlers to keep things clean on dispose
            _client.ReactionAdded -= ReactionAdded;
            _client.ReactionAdded -= ReactionRemoved;
        }
    }
}