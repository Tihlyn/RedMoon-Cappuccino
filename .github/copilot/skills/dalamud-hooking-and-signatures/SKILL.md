---
name: dalamud-hooking-and-signatures
description: Hook game functions in Dalamud / XIVLauncher plugins for FFXIV — attach detours to native code, intercept calls, read or modify arguments, and call the original. Use whenever the user wants to hook / detour / intercept a game function, react to "when the user does X" at a layer below Dalamud's services, work with FFXIVClientStructs delegate types, write a [Signature]-attribute hook, set up IGameInteropProvider, scan with ISigScanner, or asks about MinHook / Reloaded backends. Covers the strict priority for choosing a strategy (Dalamud service → CS static method → CS-declared delegate hooked by address → custom delegate + [Signature]-attribute → raw ScanText), the v9 API change that moved everything to IGameInteropProvider (HookFromAddress, HookFromSignature, HookFromSymbol, InitializeFromAttributes), the Dalamud issue #1465 gotcha that breaks InitializeFromAttributes when [PluginService] and [Signature] are mixed on the same class, and the framework-thread / Original / Dispose / try-catch discipline that keeps detours from crashing the game.
---

# Dalamud Hooking and Signatures

Hooking is how a Dalamud plugin reacts to game events that aren't surfaced by any service. You declare a delegate matching the native function's signature, point Dalamud at the function's address (via Client Structs, a byte signature, or an exported symbol), and Dalamud installs a detour that runs your code before/instead-of/after the game's. This skill covers when to reach for it, what to use, and how not to crash the game.

## When this skill applies

Trigger phrases include "hook a function", "intercept", "detour", "signature", "client structs hook", "react when the user does X" (where X is a low-level action like saving a macro, opening a duty finder window, learning an action), "byte signature", "[Signature]", "ScanText", "IGameInteropProvider", "InitializeFromAttributes". If the user is editing or creating a class with `Hook<T>` fields, this skill applies.

## First, ask: do you actually need to hook?

Hooks are powerful but expensive — a typo in a delegate signature, a missed `Original(...)` call, an exception escaping a detour, or a forgotten `Dispose()` will crash or destabilise the game. **Most party / social / overlay plugins do not need hooks at all.**

The official "Calling The Game's Code" docs prescribe a strict priority. Walk it top-to-bottom and stop at the first match:

1. **Dalamud service event or property** — the cheapest, safest, lowest-maintenance option.
2. **`FFXIVClientStructs` static method or singleton** — read it from `IFramework.Update` (with throttling). Polling at 60Hz is usually fine for UI state.
3. **CS-declared delegate** — `RaptureMacroModule.Delegates.SetSavePendingFlag` etc. Hook by address taken from the CS pointer-of-pointer; CS knows the type already.
4. **Your own delegate + `[Signature]` attribute** — when CS doesn't declare it but a stable byte pattern exists in community sig databases.
5. **Raw `ISigScanner.ScanText`** — last resort; you become responsible for the signature's stability across patches.

If the user describes a feature that walks all the way to step 4 or 5, sanity-check that the feature isn't actually doable at step 1 or 2. "Show a notification when the player enters combat" is `ICondition[ConditionFlag.InCombat]` watched in `Framework.Update` — not a hook.

## The v9 API shift — use `IGameInteropProvider` for everything

Pre-v9 hooking used static methods like `SignatureHelper.Initialize()` and `Hook.FromSignature()`. Both were removed. v9+ routes everything through the injected `IGameInteropProvider` service:

```csharp
[PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
```

The four entry points:

- `HookFromAddress<T>(nint address, T detour)` — when you have the function's address (typically from CS).
- `HookFromSignature<T>(string signature, T detour)` — when you have a byte pattern.
- `HookFromSymbol<T>(string moduleName, string exportName, T detour)` — for exported functions in modules (rare; mostly used for kernel32/user32 calls).
- `InitializeFromAttributes(object instance)` — scans the object for `[Signature]`-decorated fields and resolves them.

All return a `Hook<T>`. You **must** call `.Enable()` to actually install the detour, and `.Dispose()` to remove it.

## Pattern 3 (preferred) — Client Structs delegate, hook by address

Whenever CS declares the delegate type (named `<ContainingType>.Delegates.<MethodName>`), you get type-safe hooking with no signature maintenance:

```csharp
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System;

public unsafe class MacroSaveWatcher : IDisposable
{
    private readonly Hook<RaptureMacroModule.Delegates.SetSavePendingFlag>? hook;

    public MacroSaveWatcher(IGameInteropProvider gameInterop)
    {
        // CS exposes the function's address as a typed function pointer.
        // Cast to nint and hand it to HookFromAddress; the generic
        // argument enforces the detour signature matches the original.
        hook = gameInterop.HookFromAddress<RaptureMacroModule.Delegates.SetSavePendingFlag>(
            (nint)RaptureMacroModule.MemberFunctionPointers.SetSavePendingFlag,
            Detour);
        hook.Enable();
    }

    private void Detour(RaptureMacroModule* self, bool needsSave, uint set)
    {
        // Do your thing FIRST — before Original — if you want a snapshot
        // of pre-call state. Or after Original, if you want post-call state.
        Plugin.Log.Info("Macro changed.");

        try
        {
            hook!.Original(self, needsSave, set);
        }
        catch (Exception ex)
        {
            // An exception escaping a detour will, in the worst case, crash
            // the game. Always wrap Original (and your own logic) in try/catch
            // and log instead.
            Plugin.Log.Error(ex, "Detour failed");
        }
    }

    public void Dispose() => hook?.Dispose();
}
```

The `unsafe` modifier is required because CS structs use raw pointers. Mark only the methods (or the whole class) that genuinely need it.

When CS exposes a virtual function rather than a member function, the address comes from a vtable slot — CS still surfaces it, look for the `MemberFunctionPointers` static class on the containing type.

## Pattern 4 — your own delegate + `[Signature]` attribute

When CS doesn't declare the delegate but a stable byte pattern is known (community sig databases, IDA / Ghidra scratchpads), declare your own delegate, decorate a field with `[Signature]`, and let `InitializeFromAttributes` resolve it:

```csharp
using Dalamud.Utility.Signatures;
using Dalamud.Plugin.Services;
using System;

public unsafe class GameFunctions
{
    private delegate byte IsQuestCompletedDelegate(ushort questId);

    [Signature("E8 ?? ?? ?? ?? 41 88 84 2C")]
    private readonly IsQuestCompletedDelegate? isQuestCompleted = null;

    public GameFunctions(IGameInteropProvider gameInterop)
        => gameInterop.InitializeFromAttributes(this);

    public bool IsQuestCompleted(ushort questId)
        => isQuestCompleted is null
            ? throw new InvalidOperationException("Signature not found")
            : isQuestCompleted(questId) != 0;
}
```

`[Signature]` works on three field shapes:

- A delegate field — the field is populated with a callable function pointer (call the function as shown).
- A `Hook<T>` field — declares a hook; you also supply `[Signature(..., DetourName = nameof(Detour))]` and a method matching `T`.
- An `nint` field — populates the raw address; useful for follow-on work.

If the signature fails to resolve (game patch shifted bytes), `InitializeFromAttributes` either leaves the field null or throws depending on `[Signature]`'s `Fallibility` parameter. Default to `Fallibility.Fallible` and null-check at call sites — better than a crash on plugin load.

### The `[Signature]` + `[PluginService]` gotcha (Dalamud issue #1465)

**Combining `[PluginService]` properties and `[Signature]` fields on the same class breaks `InitializeFromAttributes`.** This is a known long-standing bug. The workaround is straightforward: separate them. Keep `[PluginService]` on your `Plugin` class (or a dedicated services holder), and put `[Signature]`-decorated fields on a different class that the plugin instantiates and passes services into via constructor:

```csharp
// ❌ Won't resolve signatures — has [PluginService] mixed in.
public class Bad {
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [Signature("...")] private readonly SomeDelegate? fn = null;
}

// ✅ Plain class, signatures resolve correctly.
public unsafe class GameFunctions {
    [Signature("...")] private readonly SomeDelegate? fn = null;
    public GameFunctions(IGameInteropProvider gi) => gi.InitializeFromAttributes(this);
}
```

## Pattern 5 — raw `ISigScanner.ScanText`

Last resort, when you need an address but `[Signature]` doesn't fit (e.g. resolving multiple addresses from one pattern, or working with a non-delegate target):

```csharp
[PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;

public void ResolveAndHook()
{
    if (!SigScanner.TryScanText("E8 ?? ?? ?? ?? 41 88 84 2C", out var addr))
    {
        Plugin.Log.Error("Signature not found");
        return;
    }
    var hook = GameInterop.HookFromAddress<MyDelegate>(addr, Detour);
    hook.Enable();
}
```

`TryScanText` is the safe variant; `ScanText` throws on miss. Other methods: `ScanModule`, `ScanData` (data section instead of text), `Resolve...` helpers for follow-the-call-target patterns.

## Detour discipline — what every hook needs

**1. Always call `Original` somewhere unless you intentionally want to suppress the game's behavior.** Skipping `Original` is how you write a "block this entirely" hook, but it's also how you accidentally break a UI element that depends on the call's side effect. Default: pass through.

**2. Always wrap your detour body (including the `Original` call) in try/catch.** An exception escaping a native callback can corrupt the stack. Log and swallow.

**3. Marshal back to the framework thread before touching Dalamud services.** Some hooks fire from non-main threads (network, audio). If your detour reads `IObjectTable`, `IPartyList`, etc., wrap with `Plugin.Framework.RunOnFrameworkThread(...)` or capture the args, return immediately, and process them later.

**4. Always `Dispose()` hooks in your plugin's `Dispose()`.** A leaked hook leaves the detour active for the rest of the session — the next plugin reload will install a *second* detour on the same address, and now you have two callbacks competing.

**5. Don't allocate or do heavy work in detours called every frame** (or worse, every input event). Compute deltas, queue work for `Framework.Update`, and consume the queue there.

**6. Don't `await` or `Task.Run` from inside a detour.** Capture the args you need into a value type, and use `Framework.RunOnTick` or a queue to do the async work later.

```csharp
// Skeleton showing all five rules at once.
public unsafe class SafeHook : IDisposable
{
    private readonly Hook<SomeDelegate>? hook;
    private readonly ConcurrentQueue<(int a, int b)> queue = new();

    public SafeHook(IGameInteropProvider gi, IFramework framework)
    {
        hook = gi.HookFromAddress<SomeDelegate>((nint)addr, Detour);
        hook.Enable();
        framework.Update += OnFrameworkUpdate;
    }

    private nint Detour(int a, int b)
    {
        try
        {
            queue.Enqueue((a, b));            // capture args, defer work
            return hook!.Original(a, b);      // always call Original
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Detour failed");
            return 0;                         // or hook.Original if you can
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        while (queue.TryDequeue(out var item))
            Process(item);                    // safe: framework thread
    }

    public void Dispose() => hook?.Dispose();
}
```

## Backends — leave the default alone

Dalamud's hooking backend is Reloaded by default. There's a `MinHook` fallback for legacy compatibility. **Don't switch backends unless you've hit a specific Reloaded incompatibility** — the default is well-tuned, and switching can introduce subtle thread-safety differences.

## Common detour patterns

**Pre-action filter** — observe an action before it happens, optionally cancel:

```csharp
private byte Detour(IntPtr self, uint actionType, uint actionId, ulong targetId, uint param, uint useType, int pvp, IntPtr unk1)
{
    if (ShouldBlock(actionId)) return 0;          // suppress entirely
    return hook!.Original(self, actionType, actionId, targetId, param, useType, pvp, unk1);
}
```

**Post-action observer** — let the call happen, then react to the result:

```csharp
private void Detour(SomeStruct* self, int x)
{
    hook!.Original(self, x);
    Plugin.Log.Info($"Post-call state: {self->SomeField}");
}
```

**Argument rewriter** — pass through with modified arguments:

```csharp
private nint Detour(nint self, int x)
    => hook!.Original(self, ClampToRange(x));
```

## Pitfalls (in priority order)

1. **`[Signature]` + `[PluginService]` on the same class breaks `InitializeFromAttributes`** (issue #1465). Separate them.
2. **An exception escaping a detour can crash the game.** Always try/catch.
3. **Forgetting `Original(...)` silently breaks game behavior.** Pass through unless you know exactly what the call does and you intentionally want to suppress it.
4. **Forgetting `Dispose()` leaves the detour active for the rest of the session.** Double-fires on plugin reload.
5. **Calling main-thread-only services from a non-main-thread detour throws.** Marshal via `IFramework.RunOnFrameworkThread`.
6. **Polluting `Framework.Update` with hook work that should have been deferred** balloons frame time. Compute on the call edge, render in `Draw()`.
7. **Stale signatures across patches.** When the next API bump or game patch ships, re-validate every `[Signature]` string. Prefer CS-typed delegates whenever they exist — CS upgrades faster than community sig databases.
8. **Don't hook to evade restrictions.** Combat plugins must show only information the player would already have, and must be cleared with the approval team *before* submission. Auto-craft / auto-roll / auto-skip / FFLogs integration / DPS parsers are forbidden regardless of how cleverly the hook is written.

## Cross-references

- For the `IGameInteropProvider` and `ISigScanner` service members, see `dalamud-services-reference`.
- For reading game state via the higher-level path that usually obviates a hook, see `dalamud-party-and-game-state`.
- For wiring the resulting hook class into your plugin's lifetime, see `dalamud-plugin-scaffold`.
- For the official-repo restrictions on what hooks may *do* (no automation, no parsers, no PvP), see `dalamud-submission-and-distribution`.
