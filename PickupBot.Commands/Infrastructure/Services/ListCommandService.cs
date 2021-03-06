﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using PickupBot.Commands.Constants;
using PickupBot.Commands.Extensions;
using PickupBot.Commands.Infrastructure.Helpers;
using PickupBot.Commands.Infrastructure.Utilities;
using PickupBot.Data.Models;
using PickupBot.Data.Repositories.Interfaces;

namespace PickupBot.Commands.Infrastructure.Services
{
    public class ListCommandService : IListCommandService
    {
        private readonly IQueueRepository _queueRepository;
        private readonly ISubscriberActivitiesRepository _activitiesRepository;

        public ListCommandService(IQueueRepository queueRepository, ISubscriberActivitiesRepository activitiesRepository)
        {
            _queueRepository = queueRepository;
            _activitiesRepository = activitiesRepository;
        }

        public async Task<PickupQueue> Create(string queueName, int? teamSize, string operators, SocketGuildUser user)
        {
            var ops = OperatorParser.Parse(operators);
            var rconEnabled = ops?.ContainsKey("-rcon") ?? true;
            if (ops?.ContainsKey("-norcon") == true)
                rconEnabled = false;

            var queue = new PickupQueue(user.Guild.Id.ToString(), queueName)
            {
                Name = queueName,
                GuildId = user.Guild.Id.ToString(),
                OwnerName = PickupHelpers.GetNickname(user),
                OwnerId = user.Id.ToString(),
                Created = DateTime.UtcNow,
                Updated = DateTime.UtcNow,
                TeamSize = teamSize ?? 4,
                IsCoop = ops?.ContainsKey("-coop") ?? false,
                Rcon = rconEnabled,
                Subscribers = new List<Subscriber>
                {
                    new Subscriber {Id = user.Id, Name = PickupHelpers.GetNickname(user)}
                },
                Host = ops?.ContainsKey("-host") ?? false ? ops["-host"]?.FirstOrDefault() : null,
                Port = int.Parse((ops?.ContainsKey("-port") ?? false ? ops["-port"]?.FirstOrDefault() : null) ?? "0"),
                Games = ops?.ContainsKey("-game") ?? false ? ops["-game"] : Enumerable.Empty<string>(),
            };

            await _queueRepository.AddQueue(queue);
            queue = await _queueRepository.FindQueue(queue.Name, user.Guild.Id.ToString());
            queue = await SaveStaticQueueMessage(queue, user.Guild);
            await _queueRepository.UpdateQueue(queue);
            return queue;
        }

        public async Task<PickupQueue> UpdateOperators(string queueName, string operators, SocketGuildUser user)
        {
            var queue = await _queueRepository.FindQueue(queueName, user.Guild.Id.ToString());
            if (queue == null) return null;
            var isAdmin = user.GuildPermissions.Has(GuildPermission.Administrator);
            if (!isAdmin && queue.OwnerId != user.Id.ToString())
            {
                return null;
            }

            var ops = OperatorParser.Parse(operators);

            if (ops?.ContainsKey("-teamsize") ?? false)
                queue.TeamSize = int.Parse(ops["-teamsize"]?.FirstOrDefault() ?? "4");
            if (ops?.ContainsKey("-rcon") ?? false)
                queue.Rcon = true;
            if (ops?.ContainsKey("-norcon") ?? false)
                queue.Rcon = false;
            if (ops?.ContainsKey("-coop") ?? false)
                queue.IsCoop = true;
            if (ops?.ContainsKey("-nocoop") ?? false)
                queue.IsCoop = false;
            if (ops?.ContainsKey("-host") ?? false)
                queue.Host = ops["-host"]?.FirstOrDefault();
            if (ops?.ContainsKey("-host") ?? false)
                queue.Port = int.Parse(ops["-port"]?.FirstOrDefault() ?? "0");
            if (ops?.ContainsKey("-game") ?? false)
                queue.Games = ops["-game"];

            queue.Updated = DateTime.UtcNow;

            queue = await SaveStaticQueueMessage(queue, user.Guild);
            await _queueRepository.UpdateQueue(queue);
            return queue;
        }

        public async Task<bool> DeleteEmptyQueue(PickupQueue queue, SocketGuild guild, ISocketMessageChannel channel, bool notify)
        {
            var result = await _queueRepository.RemoveQueue(queue.Name, queue.GuildId); //Try to remove queue if its empty
            if (result)
            {
                var queuesChannel = await PickupHelpers.GetPickupQueuesChannel(guild);
                if (!string.IsNullOrEmpty(queue.StaticMessageId))
                    await queuesChannel.DeleteMessageAsync(Convert.ToUInt64(queue.StaticMessageId));
            }

            if (!notify) return false;

            await channel.SendMessageAsync($"`{queue.Name} has been removed since everyone left.`").AutoRemoveMessage(10);

            return false;
        }

        public async Task PrintTeams(PickupQueue queue, ISocketMessageChannel channel, IGuild guild)
        {
            if (!queue.Started || queue.Teams.IsNullOrEmpty()) return;

            foreach (var team in queue.Teams)
            {
                var sb = new StringBuilder()
                    .AppendLine("**Teammates:**")
                    .AppendLine($"{string.Join(Environment.NewLine, team.Subscribers.Select(w => w.Name))}")
                    .AppendLine("")
                    .AppendLine("Your designated voice channel:")
                    .AppendLine($"[<#{team.VoiceChannel.Value}>](https://discordapp.com/channels/{guild.Id}/{team.VoiceChannel.Value})");

                await channel.SendMessageAsync(embed: new EmbedBuilder
                    {
                        Title = team.Name,
                        Description = sb.ToString(),
                        Color = Color.Red
                    }.Build())
                    .AutoRemoveMessage(120);
            }
        }

        public async Task Promote(PickupQueue queue, ITextChannel pickupChannel, IGuildUser user)
        {
            var guild = (SocketGuild)user.Guild;
            var activity = await _activitiesRepository.Find(user);
            activity.PickupPromote += 1;
            await _activitiesRepository.Update(activity);

            if (queue?.MaxInQueue <= queue?.Subscribers.Count)
            {
                await pickupChannel.SendMessageAsync("Queue is full, why the spam?").AutoRemoveMessage(10);
                return;
            }

            var role = guild.Roles.FirstOrDefault(w => w.Name == RoleNames.PickupPromote);
            if (role == null) return; //Failed to get role;
            
            var users = guild.Users.Where(w => w.Roles.Any(r => r.Id == role.Id)).ToList();
            if (!users.Any())
            {
                await pickupChannel.SendMessageAsync("No users have subscribed using the `!subscribe` command.")
                    .AutoRemoveMessage(10);
                return;
            }

            using (pickupChannel.EnterTypingState())
            {

                if (queue == null)
                {
                    var queues = await _queueRepository.AllQueues(user.GuildId.ToString());
                    var filtered = queues.Where(q => q.MaxInQueue > q.Subscribers.Count).ToArray();
                    if (filtered.Any())
                        await pickupChannel.SendMessageAsync($"There are {filtered.Length} pickup queues with spots left, check out the `!list`! - {role.Mention}")
                            .AutoRemoveMessage();
                }
                else
                {
                    var sb = BuildPromoteMessage(queue, pickupChannel);
                    var embed = new EmbedBuilder
                    {
                        Title = $"Pickup queue {queue.Name} needs more players",
                        Description = sb.ToString(),
                        Author = new EmbedAuthorBuilder { Name = "pickup-bot" },
                        Color = Color.Orange
                    }.Build();

                    foreach (var u in users)
                    {
                        await u.SendMessageAsync(embed: embed);
                        await Task.Delay(TimeSpan.FromMilliseconds(200));
                    }
                }
            }
        }

        private static StringBuilder BuildPromoteMessage(PickupQueue queue, IGuildChannel pickupChannel)
        {
            var sb = new StringBuilder()
                .AppendLine("**Current queue**")
                .AppendLine($"`{PickupHelpers.ParseSubscribers(queue)}`")
                .AppendLine("")
                .AppendLine($"**Spots left**: {queue.MaxInQueue - queue.Subscribers.Count}")
                .AppendLine($"**Team size**: {queue.TeamSize}")
                .AppendLine("")
                .AppendLine($"Just run `!add \"{queue.Name}\"` in channel <#{pickupChannel.Id}> on the **{pickupChannel.Guild.Name}** server to join!")
                .AppendLine("");

            if (!queue.Games.IsNullOrEmpty())
                sb.AppendLine($"**Game(s): ** _{string.Join(", ", queue.Games)}_");

            if (!string.IsNullOrWhiteSpace(queue.Host))
                sb.AppendLine($"**Server**: _{queue.Host ?? "ra3.se"}:{(queue.Port > 0 ? queue.Port : 27960)}_");

            return sb;
        }

        public async Task<PickupQueue> SaveStaticQueueMessage(PickupQueue queue, SocketGuild guild)
        {
            var queuesChannel = await PickupHelpers.GetPickupQueuesChannel(guild);

            var user = guild.GetUser(Convert.ToUInt64(queue.OwnerId));

            var embed = CreateStaticQueueMessageEmbed(queue, user);

            AddSubscriberFieldsToStaticQueueMessageFields(queue, embed);
            AddWaitingListFieldsToStaticQueueMessageFields(queue, embed);

            embed.WithFields(
                new EmbedFieldBuilder {Name = "\u200b", Value = "\u200b"},
                new EmbedFieldBuilder
                {
                    Name = "**Available actions**", 
                    Value = $"\u2705 - Add to pickup / remove from pickup\r\n" +
                            $"\uD83D\uDCE2 - Promote pickup"
                }
            );

            if (string.IsNullOrEmpty(queue.StaticMessageId))
            {
                var message = await queuesChannel.SendMessageAsync(embed: embed.Build());
                await message.AddReactionsAsync(new IEmote[] { new Emoji("\u2705"), new Emoji("\uD83D\uDCE2") }); // timer , new Emoji("\u23F2")

                queue.StaticMessageId = message.Id.ToString();
            }
            else
            {
                if (await queuesChannel.GetMessageAsync(Convert.ToUInt64(queue.StaticMessageId)) is IUserMessage message)
                    await message.ModifyAsync(m => { m.Embed = embed.Build(); });
            }

            return queue;
        }

        private static EmbedBuilder CreateStaticQueueMessageEmbed(PickupQueue queue, IUser user)
        {
            var embed = new EmbedBuilder
            {
                Title = queue.Name,
                Author = new EmbedAuthorBuilder { Name = PickupHelpers.GetNickname(user), IconUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl() },
                Color = Color.Gold, 
                Fields = new List<EmbedFieldBuilder>
                {
                    new EmbedFieldBuilder { Name = "**Created by**", Value = PickupHelpers.GetNickname(user), IsInline = true },
                    new EmbedFieldBuilder
                    {
                        Name = "**Game(s)**",
                        Value = string.Join(", ", queue.Games.IsNullOrEmpty() ? new[] { "No game defined" } : queue.Games),
                        IsInline = true
                    },
                    new EmbedFieldBuilder { Name = "**Started**", Value = queue.Started ? "Yes" : "No", IsInline = true },
                    new EmbedFieldBuilder { Name = "**Host**", Value = queue.Host ?? "No host defined", IsInline = true },
                    new EmbedFieldBuilder
                    {
                        Name = "**Port**",
                        Value = queue.Port == 0 ? "No port defined" : queue.Port.ToString(),
                        IsInline = true
                    },
                    new EmbedFieldBuilder { Name = "**Team size**", Value = queue.TeamSize, IsInline = true },
                    new EmbedFieldBuilder { Name = "**Coop**", Value = queue.IsCoop ? "Yes" : "No", IsInline = true },
                    new EmbedFieldBuilder { Name = "**Created**", Value = queue.Created.ToString("yyyy-MM-dd\r\nHH:mm:ss 'UTC'"), IsInline = true },
                    new EmbedFieldBuilder { Name = "**Last updated**", Value = queue.Updated.ToString("yyyy-MM-dd\r\nHH:mm:ss 'UTC'"), IsInline = true }
                }
            };

            return embed;
        }

        private static void AddSubscriberFieldsToStaticQueueMessageFields(PickupQueue queue, EmbedBuilder embed)
        {
            var sb = new StringBuilder();
            queue.Subscribers.ForEach(p => sb.AppendLine(p.Name));

            embed.WithFields(new EmbedFieldBuilder
            {
                Name = $"**Players in queue [{queue.Subscribers.Count}/{queue.MaxInQueue}]**",
                Value = queue.Subscribers.IsNullOrEmpty() ? "No players in queue" : sb.ToString(),
                IsInline = true
            });

            sb.Clear();
        }

        private static void AddWaitingListFieldsToStaticQueueMessageFields(PickupQueue queue, EmbedBuilder embed)
        {
            var sb = new StringBuilder();
            queue.WaitingList.Select((p, i) => $"{i}. {p.Name}").ToList().ForEach(p => sb.AppendLine(p));

            embed.WithFields(new EmbedFieldBuilder
            {
                Name = $"**Players in waiting list [{queue.WaitingList.Count}]**",
                Value = queue.WaitingList.IsNullOrEmpty() ? "No players in waiting list" : sb.ToString(),
                IsInline = true
            });

            sb.Clear();
        }
    }

    public interface IListCommandService
    {
        Task<PickupQueue> Create(string queueName, int? teamSize, string operators, SocketGuildUser user);
        Task<PickupQueue> UpdateOperators(string queueName, string operators, SocketGuildUser user);
        Task<bool> DeleteEmptyQueue(PickupQueue queue, SocketGuild guild, ISocketMessageChannel channel, bool notify);
        Task PrintTeams(PickupQueue queue, ISocketMessageChannel channel, IGuild guild);
        Task Promote(PickupQueue queue, ITextChannel pickupChannel, IGuildUser user);
        Task<PickupQueue> SaveStaticQueueMessage(PickupQueue queue, SocketGuild guild);
    }
}
