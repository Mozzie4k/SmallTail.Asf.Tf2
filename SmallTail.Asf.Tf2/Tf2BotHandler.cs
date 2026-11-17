using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using SteamKit2;

namespace SmallTail.Asf.Tf2;

[Export(typeof(IPlugin))]
public class Tf2Plugin : IPlugin, IBotCommand2, IBotSteamClient
{
    public string Name => "SmallTail TF2";
    public Version Version => typeof(Tf2Plugin).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>How long we wait for the TF2 game coordinator before giving up on a bot.</summary>
    private static readonly TimeSpan GcTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Tf2BotHandler> _tf2BotHandlers = new();

    public Task OnLoaded()
    {
        ASF.ArchiLogger.LogGenericInfo("SmallTail TF2 loaded! ^-^");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    //  IBotSteamClient
    // ---------------------------------------------------------------------

    public Task OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<ClientMsgHandler>?> OnBotSteamHandlersInit(Bot bot)
    {
        var handler = new Tf2BotHandler(bot);
        _tf2BotHandlers[bot.BotName] = handler;

        return Task.FromResult<IReadOnlyCollection<ClientMsgHandler>?>(new ClientMsgHandler[] { handler });
    }

    // ---------------------------------------------------------------------
    //  IBotCommand2
    // ---------------------------------------------------------------------

    public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var command = args[0].ToLowerInvariant();

        Func<Bot, Tf2BotHandler, string[], Task<string?>>? handler = command switch
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

        // All of these use or delete items - require Master
        if (access < EAccess.Master)
        {
            return null;
        }

        HashSet<Bot>? bots;

        if (args.Length >= 2)
        {
            // Lets ASF resolve "ASF", "bot1,bot2", "@all", "bot1..bot5" etc.
            bots = Bot.GetBots(args[1]);

            if (bots is null || bots.Count == 0)
            {
                return $"<{args[1]}> Bot not found";
            }
        }
        else
        {
            // No bot given -> act on the bot that received the message.
            // Insert its name so args[2]/args[3] still line up in the handlers.
            bots = new HashSet<Bot> { bot };
            args = new[] { args[0], bot.BotName };
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
                return $"<{b.BotName}> Timed out waiting for the TF2 game coordinator (does this account own TF2 / is it in-game elsewhere?)";
            }
            catch (Exception e)
            {
                return $"<{b.BotName}> Error: {e.Message}";
            }
        })).ConfigureAwait(false);

        var responses = results.Where(r => !string.IsNullOrEmpty(r)).ToList();

        return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
    }

    // ---------------------------------------------------------------------
    //  Shared GC session helper
    //  Connects, waits (with timeout) for account + items, runs the action,
    //  and ALWAYS disconnects afterwards so a failing bot never stays "in TF2".
    // ---------------------------------------------------------------------

    private static async Task<string?> WithGc(Bot bot, Tf2BotHandler tf2BotHandler, Func<Task<string?>> action)
    {
        var waiters = tf2BotHandler.Connect();

        try
        {
            await waiters.AccountLoaded.Task.WaitAsync(GcTimeout).ConfigureAwait(false);
            await waiters.ItemsLoaded.Task.WaitAsync(GcTimeout).ConfigureAwait(false);

            return await action().ConfigureAwait(false);
        }
        finally
        {
            await tf2BotHandler.Disconnect().ConfigureAwait(false);
        }
    }

    private static bool TryParseAmount(string value, int available, out int amount)
    {
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            amount = available;
            return true;
        }

        return int.TryParse(value, out amount) && amount > 0;
    }

    // ---------------------------------------------------------------------
    //  Command handlers
    //  Usage:
    //    tf2slots   [Bots]
    //    tf2premium [Bots]
    //    tf2useid   [Bots] <itemId>
    //    tf2usedef  [Bots] <defindex> <amount|all>
    //    tf2rm      [Bots] <itemId>
    //    tf2rmdef   [Bots] <defindex> <amount|all>
    //    tf2bec     [Bots]
    //    tf2beu     [Bots] <amount|all>
    // ---------------------------------------------------------------------

    private static Task<string?> HandleTf2Slots(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        return WithGc(bot, tf2BotHandler, () =>
        {
            var used = tf2BotHandler.Items.Count;
            var total = tf2BotHandler.BackpackSlots;

            return Task.FromResult<string?>($"<{bot.BotName}> Slots: {used}/{total}");
        });
    }

    private static Task<string?> HandleTf2Premium(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        return WithGc(bot, tf2BotHandler, () =>
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Premium: {(tf2BotHandler.IsPremium ? "yes" : "no")}");
        });
    }

    private static Task<string?> HandleTf2UseId(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        if (args.Length < 3 || !ulong.TryParse(args[2], out var itemId))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Usage: tf2useid [Bots] <itemId>");
        }

        return WithGc(bot, tf2BotHandler, async () =>
        {
            var item = tf2BotHandler.Items.FirstOrDefault(i => i.Id == itemId);

            if (item is null)
            {
                return $"<{bot.BotName}> Item {itemId} not found";
            }

            await tf2BotHandler.UseItem(item.Id).ConfigureAwait(false);

            return $"<{bot.BotName}> Used item {itemId}";
        });
    }

    private static Task<string?> HandleTf2UseDef(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        if (args.Length < 4 || !uint.TryParse(args[2], out var defIndex))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Usage: tf2usedef [Bots] <defindex> <amount|all>");
        }

        return WithGc(bot, tf2BotHandler, async () =>
        {
            var items = tf2BotHandler.Items.Where(i => i.DefIndex == defIndex).ToList();

            if (items.Count == 0)
            {
                return $"<{bot.BotName}> No items with defindex {defIndex}";
            }

            if (!TryParseAmount(args[3], items.Count, out var amount))
            {
                return $"<{bot.BotName}> Invalid amount: {args[3]}";
            }

            var used = 0;

            foreach (var item in items.Take(amount))
            {
                await tf2BotHandler.UseItem(item.Id).ConfigureAwait(false);
                used++;
            }

            return $"<{bot.BotName}> Used {used}/{items.Count} items with defindex {defIndex}";
        });
    }

    private static Task<string?> HandleTf2Rm(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        if (args.Length < 3 || !ulong.TryParse(args[2], out var itemId))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Usage: tf2rm [Bots] <itemId>");
        }

        return WithGc(bot, tf2BotHandler, async () =>
        {
            var item = tf2BotHandler.Items.FirstOrDefault(i => i.Id == itemId);

            if (item is null)
            {
                return $"<{bot.BotName}> Item {itemId} not found";
            }

            await tf2BotHandler.DeleteItem(item.Id).ConfigureAwait(false);

            return $"<{bot.BotName}> Deleted item {itemId}";
        });
    }

    private static Task<string?> HandleTf2RmDef(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        // FIX: was "args.Length < 2", which let args[2] throw IndexOutOfRange
        if (args.Length < 4 || !uint.TryParse(args[2], out var defIndex))
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Usage: tf2rmdef [Bots] <defindex> <amount|all>");
        }

        return WithGc(bot, tf2BotHandler, async () =>
        {
            var items = tf2BotHandler.Items.Where(i => i.DefIndex == defIndex).ToList();

            if (items.Count == 0)
            {
                return $"<{bot.BotName}> No items with defindex {defIndex}";
            }

            if (!TryParseAmount(args[3], items.Count, out var amount))
            {
                return $"<{bot.BotName}> Invalid amount: {args[3]}";
            }

            var deleted = 0;

            foreach (var item in items.Take(amount))
            {
                await tf2BotHandler.DeleteItem(item.Id).ConfigureAwait(false);
                deleted++;
            }

            return $"<{bot.BotName}> Deleted {deleted}/{items.Count} items with defindex {defIndex}";
        });
    }

    private static Task<string?> HandleTf2ExpanderCount(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        return WithGc(bot, tf2BotHandler, () =>
        {
            var count = tf2BotHandler.Items.Count(i => i.DefIndex == Tf2BotHandler.BackpackExpanderDefIndex);

            return Task.FromResult<string?>($"<{bot.BotName}> Backpack expanders: {count}");
        });
    }

    private static Task<string?> HandleTf2ExpanderUse(Bot bot, Tf2BotHandler tf2BotHandler, string[] args)
    {
        if (args.Length < 3)
        {
            return Task.FromResult<string?>($"<{bot.BotName}> Usage: tf2beu [Bots] <amount|all>");
        }

        return WithGc(bot, tf2BotHandler, async () =>
        {
            var expanders = tf2BotHandler.Items
                .Where(i => i.DefIndex == Tf2BotHandler.BackpackExpanderDefIndex)
                .ToList();

            if (expanders.Count == 0)
            {
                return $"<{bot.BotName}> No backpack expanders";
            }

            if (!TryParseAmount(args[2], expanders.Count, out var amount))
            {
                return $"<{bot.BotName}> Invalid amount: {args[2]}";
            }

            var used = 0;

            foreach (var expander in expanders.Take(amount))
            {
                await tf2BotHandler.UseItem(expander.Id).ConfigureAwait(false);
                used++;
            }

            return $"<{bot.BotName}> Used {used}/{expanders.Count} backpack expanders";
        });
    }
}
