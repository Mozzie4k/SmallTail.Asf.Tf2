using System.Collections.Concurrent;
using System.Composition;
using System.Reflection;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using SteamKit2;
using SteamKit2.GC.TF2.Internal;

namespace SmallTail.Asf.TfBackpack;

[Export(typeof(IPlugin))]
public class Tf2Plugin : IPlugin, IBotCommand2, IBotSteamClient
{
    public string Name => "SmallTail TF2";
    public Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? throw new InvalidOperationException(nameof(Version));

    /// <summary>How long to wait for the TF2 game coordinator before giving up on a bot.</summary>
    private static readonly TimeSpan GcTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Tf2BotHandler> _tf2BotHandlers = new();

    public Task OnLoaded()
    {
        ASF.ArchiLogger.LogGenericInfo("SmallTail TF2 loaded! ^-^");
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    //  Command dispatch
    //  [Bots] is resolved by ASF itself: "ASF", "bot1,bot2", "@all", etc.
    //  If [Bots] is omitted, the bot that received the message is used.
    // -----------------------------------------------------------------

    public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0)
    {
        if (args.Length == 0)
        {
            return null;
        }

        Func<Bot, Tf2BotHandler, string[], Task<string?>>? handler = args[0].ToLowerInvariant() switch
        {
            "tf2slots"   => HandleTf2Slots,
            "tf2premium" => HandleTf2Premium,

            "tf2useid"   => HandleTf2UseId,
            "tf2usedef"  => HandleTf2UseDef,

            "tf2rm"      => HandleTf2Rm,
            "tf2rmdef"   => HandleTf2RmDef,

            "tf2bec"     => HandleTf2ExpanderCount,
            "tf2beu"     => HandleTf2ExpanderUse,

            _ => null
        };

        if (handler is null)
        {
            return null; // not our command
        }

        // These commands can use/delete items - require Master
        if (access < EAccess.Master)
        {
            return null;
        }

        HashSet<Bot>? bots;

        if (args.Length >= 2)
        {
            bots = Bot.GetBots(args[1]);

            if (bots is null || bots.Count == 0)
            {
                return $"<{args[1]}> Bot not found";
            }
        }
        else
        {
            // No [Bots] given -> use the current bot and insert its name so args[2..] still line up
            bots = [bot];
            args = [args[0], bot.BotName];
        }

        var results = await Utilities.InParallel(bots.Select(async b =>
        {
            if (!b.IsConnectedAndLoggedOn)
            {
                return $"<{b.BotName}> Bot is not connected";
            }

            if (!_tf2BotHandlers.TryGetValue(b.BotName, out var tf2BotHandler))
            {
                return $"<{b.BotName}> Failed to get Tf2BotHandler";
            }

            try
            {
                return await handler(b, tf2BotHandler, args).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return $"<{b.BotName}> Timed out waiting for TF2 game coordinator (does this account own TF2 / is it in-game elsewhere?)";
            }
            catch (Exception e)
            {
                return $"<{b.BotName}> Error: {e.Message}";
            }
        })).ConfigureAwait(false);

        var responses = results.Where(r => !string.IsNullOrEmpty(r)).ToList();

        return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
    }

    // -----------------------------------------------------------------
    //  Shared GC session helper: connect, wait (with timeout), run,
    //  and ALWAYS disconnect so a failing bot never stays "in TF2".
    // -----------------------------------------------------------------

    private static async Task<string?> WithGc(Tf2BotHandler tf2BotHandler, bool needItems, Func<Task<string?>> action)
    {
        var waiters = await tf2BotHandler.Connect().ConfigureAwait(false);

        try
        {
            await waiters.AccountLoaded.Task.WaitAsync(GcTimeout).ConfigureAwait(false);

            if (needItems)
            {
                await waiters.ItemsLoaded.Task.WaitAsync(GcTimeout).ConfigureAwait(false);
            }

            return await action().ConfigureAwait(false);
        }
        finally
        {
            await tf2BotHandler.Disconnect().ConfigureAwait(false);
        }
    }

    private static bool TryParseCount(string value, out int count)
    {
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            count = int.MaxValue;
            return true;
        }

        return int.TryParse(value, out count) && count > 0;
    }

    // -----------------------------------------------------------------
    //  Handlers
    // -----------------------------------------------------------------

    private static Task<string?> HandleTf2Slots(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
        => WithGc(tf2BotHandler, needItems: false, () =>
            Task.FromResult<string?>($"<{bot.BotName}> {tf2BotHandler.SlotCount}"));

    private static Task<string?> HandleTf2Premium(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
        => WithGc(tf2BotHandler, needItems: false, () =>
            Task.FromResult<string?>($"<{bot.BotName}> {tf2BotHandler.IsPremium}"));

    private static Task<string?> HandleTf2UseId(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        if (args.Length < 3)
        {
            return Task.FromResult<string?>($"<{bot.BotName}> <itemId> argument is required");
        }

        if (!ulong.TryParse(args[2], out var itemId))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Bad item id");
        }

        return WithGc(tf2BotHandler, needItems: false, () =>
        {
            tf2BotHandler.UseItem(itemId);
            return Task.FromResult<string?>($"<{bot.BotName}> Used");
        });
    }

    private static Task<string?> HandleTf2UseDef(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        if (args.Length < 3)
        {
            return Task.FromResult<string?>($"<{bot.BotName}> <itemDef> argument is required");
        }

        if (!uint.TryParse(args[2], out var defIndex))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Bad item def");
        }

        if (args.Length < 4)
        {
            return Task.FromResult<string?>($"<{bot.BotName}> <count | all> argument is required");
        }

        if (!TryParseCount(args[3], out var count))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Bad count");
        }

        return WithGc(tf2BotHandler, needItems: true, async () =>
        {
            var items = tf2BotHandler.Items
                .Where(i => i.def_index == defIndex)
                .Take(count)
                .ToList();

            await tf2BotHandler.UseItems(items).ConfigureAwait(false);

            return $"<{bot.BotName}> Used {items.Count}";
        });
    }

    private static Task<string?> HandleTf2Rm(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        if (args.Length < 3)
        {
            return Task.FromResult<string?>($"<{bot.BotName}> <itemId | all> argument is required");
        }

        if (args[2].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return WithGc(tf2BotHandler, needItems: true, async () =>
            {
                var originalItemCount = tf2BotHandler.Items.Count;
                var currentItemCount = 0;

                for (var i = 0; i < 3; i++)
                {
                    currentItemCount = tf2BotHandler.Items.Count;

                    if (currentItemCount == 0)
                    {
                        break;
                    }

                    await tf2BotHandler.DeleteItems(tf2BotHandler.Items.ToList()).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }

                return $"<{bot.BotName}> Item count {originalItemCount} -> {currentItemCount}";
            });
        }

        if (!ulong.TryParse(args[2], out var itemId))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Bad item id");
        }

        return WithGc(tf2BotHandler, needItems: false, () =>
        {
            tf2BotHandler.DeleteItem(itemId);
            return Task.FromResult<string?>($"<{bot.BotName}> Deleted");
        });
    }

    private static Task<string?> HandleTf2RmDef(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        // FIX: original checked args.Length < 2 and then read args[2]
        if (args.Length < 3)
        {
            return Task.FromResult<string?>($"<{bot.BotName}> <itemDef> argument is required");
        }

        if (!uint.TryParse(args[2], out var defIndex))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Bad item def");
        }

        return WithGc(tf2BotHandler, needItems: true, async () =>
        {
            var items = tf2BotHandler.Items
                .Where(i => i.def_index == defIndex)
                .ToList();

            await tf2BotHandler.DeleteItems(items).ConfigureAwait(false);

            return $"<{bot.BotName}> Deleted {items.Count}";
        });
    }

    private static Task<string?> HandleTf2ExpanderCount(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
        => WithGc(tf2BotHandler, needItems: true, () =>
        {
            var count = tf2BotHandler.Items.Count(i => i.def_index == Tf2Items.BackpackExpander);
            return Task.FromResult<string?>($"<{bot.BotName}> {count}");
        });

    private static Task<string?> HandleTf2ExpanderUse(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        if (args.Length < 3)
        {
            return Task.FromResult<string?>($"<{bot.BotName}> <count | all> argument is required");
        }

        if (!TryParseCount(args[2], out var count))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Bad count");
        }

        return WithGc(tf2BotHandler, needItems: true, async () =>
        {
            var expanders = tf2BotHandler.Items
                .Where(i => i.def_index == Tf2Items.BackpackExpander)
                .Take(count)
                .ToList();

            await tf2BotHandler.UseItems(expanders).ConfigureAwait(false);

            return $"<{bot.BotName}> Used {expanders.Count}";
        });
    }

    // -----------------------------------------------------------------
    //  IBotSteamClient (unchanged from original)
    // -----------------------------------------------------------------

    public Task OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager)
    {
        var tfBotHandler = new Tf2BotHandler(bot);

        if (!_tf2BotHandlers.TryAdd(bot.BotName, tfBotHandler))
        {
            return Task.CompletedTask;
        }

        callbackManager.Subscribe<SteamGameCoordinator.MessageCallback>(c => tfBotHandler.OnGCMessage(c));

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<ClientMsgHandler>?> OnBotSteamHandlersInit(Bot bot)
    {
        return Task.FromResult<IReadOnlyCollection<ClientMsgHandler>?>(null);
    }
}
