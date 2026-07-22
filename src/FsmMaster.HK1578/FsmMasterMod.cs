// SPDX-License-Identifier: EUPL-1.2
using Modding;
using Modding.Menu;
using Modding.Menu.Config;
using UnityEngine;
using UnityEngine.UI;
#if HK1432
using CancelDelegate = Modding.Utils.Net472Interop.Action<UnityEngine.UI.MenuSelectable>;
#elif HK1315
// hk1315 has no Modding.Utils.Net472Interop type at all (checked: zero hits for "Net472Interop" in
// agent-context/hk1315/Managed/Assembly-CSharp.dll) - it has its own differently-named equivalent
// instead, found only by letting the build fail and reading the real CS0029 target type: this game
// version's HorizontalOptionConfig.CancelAction wants Shims.NET.System.Action<T>, not
// Modding.Utils.Net472Interop.Action<T> or plain System.Action<T>. A third genuine variant, not a
// guess - confirmed present via a "Shims.NET" string hit in the same Assembly-CSharp.dll.
using CancelDelegate = Shims.NET.System.Action<UnityEngine.UI.MenuSelectable>;
#else
using CancelDelegate = System.Action<UnityEngine.UI.MenuSelectable>;
#endif

namespace FsmMaster;

public class FsmMasterMod : Mod, IGlobalSettings<FsmMasterGlobalSettings>, ICustomMenuMod
{
    internal static FsmMasterMod? Instance { get; private set; }

    private static FsmMasterGlobalSettings _settings = new();

    internal static FsmMasterGlobalSettings Settings => _settings;

    public void OnLoadGlobal(FsmMasterGlobalSettings settings) => _settings = settings;

    public FsmMasterGlobalSettings OnSaveGlobal() => _settings;

    // No ILocalSettings<T> here - FsmMaster has no per-save data of its own (FsmMasterSaveSettings on
    // the hk1221 loader is an empty placeholder for the same reason), unlike DebugMod's own
    // SaveSettings.
    public override void Initialize()
    {
        Instance = this;

        On.HutongGames.PlayMaker.Fsm.Preprocess += FsmActivationPatches.OnFsmPreprocess;
        ModHooks.CursorHook += FsmActivationPatches.OnCursorHook;
#if !HK1315
        // See the #if !HK1315 block in FocusOnHoverSuppressionPatch.cs - this game version's HookGen
        // pass never generated a hook for HollowKnightInputModule.ProcessMove, so the type doesn't
        // exist as On.InControl.HollowKnightInputModule here at all.
        On.InControl.HollowKnightInputModule.ProcessMove += FocusOnHoverSuppressionPatch.OnProcessMove;
#endif
        DebugModSavestateCompat.TryHook();

        var driverObject = new GameObject("FsmMasterDriver");
        driverObject.AddComponent<FsmMasterDriver>();
        Object.DontDestroyOnLoad(driverObject);

        Log("FsmMaster initialized.");
    }

    // Generated at build time from this project's target prefix and $(Version) - hk1432 compiles this
    // same file with its own prefix, so the two builds identify themselves distinctly in the mod list.
    public override string GetVersion() => BuildInfo.ReleaseName;

    // ICustomMenuMod - a minimal settings screen (auto-load-last-configuration and the graph diagnostics
    // toggle). Built by hand with ContentArea.AddHorizontalOption/MenuBuilder rather than going through
    // MenuUtils.CreateMenuScreen(List<IMenuMod.MenuEntry>): that struct's Saver/Loader fields are
    // Action<int>/Func<int> (or hk1432's own Net472Interop.Action<int>) - closed generic delegates over
    // a value type - and hk1432's old Mono runtime throws TypeLoadException trying to load that closed
    // generic the moment the menu is built, no matter how the delegate is constructed (lambda, method
    // group, or pure reflection all failed identically). MenuSetting.ApplySetting/RefreshSetting are
    // ordinary named delegates instead of generic ones, so building the option row directly through them
    // avoids ever instantiating Action<int>/Func<int> at all.
    public bool ToggleButtonInsideMenu => false;

    public MenuScreen GetMenuScreen(MenuScreen modListMenu, ModToggleDelegates? toggleDelegates)
    {
        var builder = MenuUtils.CreateMenuBuilderWithBackButton("FsmMaster", modListMenu, out _);
        builder.AddContent(RegularGridLayout.CreateVerticalLayout(105f), c =>
        {
            AddToggle(
                c,
                modListMenu,
                "Auto-Load Last Configuration",
                "Reapply the last active FSM edits automatically when their FSM is next created.",
                ApplyAutoLoadLastConfiguration,
                RefreshAutoLoadLastConfiguration);
            AddToggle(
                c,
                modListMenu,
                "Graph Diagnostics",
                "Log a periodic timing breakdown of the graph overlay's rendering while it's open.",
                ApplyGraphDiagnostics,
                RefreshGraphDiagnostics);
        });

        return builder.Build();
    }

    private static void AddToggle(
        ContentArea c,
        MenuScreen returnScreen,
        string name,
        string description,
        MenuSetting.ApplySetting apply,
        MenuSetting.RefreshSetting refresh)
    {
        c.AddHorizontalOption(
            name,
            new HorizontalOptionConfig
            {
                ApplySetting = apply,
                RefreshSetting = refresh,
                CancelAction = (CancelDelegate)(_ => UIManager.instance.GoToDynamicMenu(returnScreen)),
                Description = new DescriptionInfo { Text = description },
                Label = name,
                Options = new[] { "Off", "On" },
                Style = HorizontalOptionStyle.VanillaStyle,
            },
            out var horizontalOption);
        horizontalOption.menuSetting.RefreshValueFromGameSettings();
    }

    private static void ApplyAutoLoadLastConfiguration(MenuSetting self, int settingIndex) =>
        Settings.AutoLoadLastConfiguration = settingIndex == 1;

    private static void RefreshAutoLoadLastConfiguration(MenuSetting self, bool alsoApplySetting) =>
        self.optionList.SetOptionTo(Settings.AutoLoadLastConfiguration ? 1 : 0);

    private static void ApplyGraphDiagnostics(MenuSetting self, int settingIndex) =>
        Settings.GraphDiagnostics = settingIndex == 1;

    private static void RefreshGraphDiagnostics(MenuSetting self, bool alsoApplySetting) =>
        self.optionList.SetOptionTo(Settings.GraphDiagnostics ? 1 : 0);
}
