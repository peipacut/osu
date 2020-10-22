using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Rulesets.Replays;
using osu.Game.Rulesets.Replays.Types;

namespace osu.Game.Online.Spectator
{
    public class SpectatorStreamingClient : Component, ISpectatorClient
    {
        private HubConnection connection;

        private readonly List<string> watchingUsers = new List<string>();

        private readonly IBindable<APIState> apiState = new Bindable<APIState>();

        private bool isConnected;

        [Resolved]
        private IAPIProvider api { get; set; }

        [Resolved]
        private IBindable<WorkingBeatmap> beatmap { get; set; }

        [BackgroundDependencyLoader]
        private void load()
        {
            apiState.BindTo(api.State);
            apiState.BindValueChanged(apiStateChanged, true);
        }

        private void apiStateChanged(ValueChangedEvent<APIState> state)
        {
            switch (state.NewValue)
            {
                case APIState.Failing:
                case APIState.Offline:
                    connection?.StopAsync();
                    connection = null;
                    break;

                case APIState.Online:
                    Task.Run(connect);
                    break;
            }
        }

#if DEBUG
        private const string endpoint = "http://localhost:5009/spectator";
#else
        private const string endpoint = "https://spectator.ppy.sh/spectator";
#endif

        private async Task connect()
        {
            if (connection != null)
                return;

            connection = new HubConnectionBuilder()
                         .WithUrl(endpoint, options =>
                         {
                             options.Headers.Add("Authorization", $"Bearer {api.AccessToken}");
                         })
                         .AddMessagePackProtocol()
                         .Build();

            // until strong typed client support is added, each method must be manually bound (see https://github.com/dotnet/aspnetcore/issues/15198)
            connection.On<string, int>(nameof(ISpectatorClient.UserBeganPlaying), ((ISpectatorClient)this).UserBeganPlaying);
            connection.On<string, FrameDataBundle>(nameof(ISpectatorClient.UserSentFrames), ((ISpectatorClient)this).UserSentFrames);
            connection.On<string, int>(nameof(ISpectatorClient.UserFinishedPlaying), ((ISpectatorClient)this).UserFinishedPlaying);

            connection.Closed += async ex =>
            {
                isConnected = false;
                if (ex != null) await tryUntilConnected();
            };

            await tryUntilConnected();

            async Task tryUntilConnected()
            {
                while (api.State.Value == APIState.Online)
                {
                    try
                    {
                        // reconnect on any failure
                        await connection.StartAsync();

                        // success
                        isConnected = true;
                        break;
                    }
                    catch
                    {
                        await Task.Delay(5000);
                    }
                }
            }
        }

        Task ISpectatorClient.UserBeganPlaying(string userId, int beatmapId)
        {
            if (connection.ConnectionId != userId)
            {
                if (watchingUsers.Contains(userId))
                {
                    Console.WriteLine($"{connection.ConnectionId} received began playing for already watched user {userId}");
                }
                else
                {
                    Console.WriteLine($"{connection.ConnectionId} requesting watch other user {userId}");
                    WatchUser(userId);
                    watchingUsers.Add(userId);
                }
            }
            else
            {
                Console.WriteLine($"{connection.ConnectionId} Received user playing event for self {beatmapId}");
            }

            return Task.CompletedTask;
        }

        Task ISpectatorClient.UserFinishedPlaying(string userId, int beatmapId)
        {
            Console.WriteLine($"{connection.ConnectionId} Received user finished event {beatmapId}");
            return Task.CompletedTask;
        }

        Task ISpectatorClient.UserSentFrames(string userId, FrameDataBundle data)
        {
            Console.WriteLine($"{connection.ConnectionId} Received frames from {userId}: {data.Frames.First()}");
            return Task.CompletedTask;
        }

        public void BeginPlaying(int beatmapId)
        {
            if (!isConnected) return;

            connection.SendAsync(nameof(ISpectatorServer.BeginPlaySession), beatmapId);
        }

        public void SendFrames(FrameDataBundle data)
        {
            if (!isConnected) return;

            connection.SendAsync(nameof(ISpectatorServer.SendFrameData), data);
        }

        public void EndPlaying(int beatmapId)
        {
            if (!isConnected) return;

            connection.SendAsync(nameof(ISpectatorServer.EndPlaySession), beatmapId);
        }

        public void WatchUser(string userId)
        {
            if (!isConnected) return;

            connection.SendAsync(nameof(ISpectatorServer.StartWatchingUser), userId);
        }

        public void HandleFrame(ReplayFrame frame)
        {
            if (frame is IConvertibleReplayFrame convertible)
                // TODO: don't send a bundle for each individual frame
                SendFrames(new FrameDataBundle(new[] { convertible.ToLegacy(beatmap.Value.Beatmap) }));
        }
    }
}
