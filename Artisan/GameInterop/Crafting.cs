﻿using Artisan.CraftingLogic;
using Artisan.CraftingLogic.CraftData;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Linq;

namespace Artisan.GameInterop;

// state of the normal (non-quick) crafting process
// manages the 'inner loop' (executing actions to complete a single craft)
public static unsafe class Crafting
{
    public enum State
    {
        IdleNormal, // we're not crafting - the default state of the game
        IdleBetween, // we've finished a craft and have not yet started another, sitting in the menu
        WaitStart, // we're waiting for a new craft to start
        InProgress, // crafting is in progress, waiting for next action
        WaitAction, // we've executed an action and are waiting for results
        WaitFinish, // we're waiting for a craft to end (success / failure / cancel)
        InvalidState, // we're in a state we probably shouldn't be, such as reloading the plugin mid-craft
    }

    public static State CurState { get; private set; } = State.InvalidState;
    public static event Action<State>? StateChanged;

    public static Lumina.Excel.GeneratedSheets.Recipe? CurRecipe { get; private set; }
    public static CraftState? CurCraft { get; private set; }
    public static StepState? CurStep { get; private set; }
    public static bool IsTrial { get; private set; }

    public delegate void CraftStartedDelegate(Lumina.Excel.GeneratedSheets.Recipe recipe, CraftState craft, StepState initialStep, bool trial);
    public static event CraftStartedDelegate? CraftStarted;

    // note: step index increases for most actions (except final appraisal / careful observation / heart&soul)
    public delegate void CraftAdvancedDelegate(Lumina.Excel.GeneratedSheets.Recipe recipe, CraftState craft, StepState step);
    public static event CraftAdvancedDelegate? CraftAdvanced;

    // note: final action that completes/fails a craft does not advance step index
    public delegate void CraftFinishedDelegate(Lumina.Excel.GeneratedSheets.Recipe recipe, CraftState craft, StepState finalStep, bool cancelled);
    public static event CraftFinishedDelegate? CraftFinished;

    private static Skills _pendingAction;
    private static StepState? _predictedNextStep;

    public static void Dispose()
    {
        ActionManagerEx.ActionUsed -= OnActionUsed;
    }

    // note: this uses current character stats & equipped gear
    public static CraftState BuildCraftStateForRecipe(Lumina.Excel.GeneratedSheets.Recipe recipe)
    {
        var lt = recipe.RecipeLevelTable.Value;
        var weapon = Svc.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.Item>()?.GetRow(InventoryManager.Instance()->GetInventorySlot(InventoryType.EquippedItems, 0)->ItemID);
        var res = new CraftState()
        {
            StatCraftsmanship = CharacterInfo.Craftsmanship,
            StatControl = CharacterInfo.Control,
            StatCP = (int)CharacterInfo.MaxCP,
            StatLevel = CharacterInfo.CharacterLevel ?? 0,
            UnlockedManipulation = CharacterInfo.IsManipulationUnlocked(),
            Specialist = InventoryManager.Instance()->GetInventorySlot(InventoryType.EquippedItems, 13)->ItemID != 0, // specialist == job crystal equipped
            Splendorous = weapon?.LevelEquip == 90 && weapon?.Rarity >= 4,
            CraftCollectible = recipe.ItemResult.Value?.IsCollectable ?? false,
            CraftExpert = recipe.IsExpert,
            CraftLevel = lt?.ClassJobLevel ?? 0,
            CraftDurability = Calculations.RecipeDurability(recipe),
            CraftProgress = Calculations.RecipeDifficulty(recipe),
            CraftProgressDivider = lt?.ProgressDivider ?? 180,
            CraftProgressModifier = lt?.ProgressModifier ?? 100,
            CraftQualityDivider = lt?.QualityDivider ?? 180,
            CraftQualityModifier = lt?.QualityModifier ?? 180,
            CraftQualityMax = Calculations.RecipeMaxQuality(recipe),
        };

        if (res.CraftCollectible)
        {
            var breakpoints = Svc.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.CollectablesShopItem>()?.FirstOrDefault(x => x.Item.Row == recipe.ItemResult.Row)?.CollectablesShopRefine.Value;
            if (breakpoints != null)
            {
                res.CraftQualityMin1 = breakpoints.LowCollectability * 10;
                res.CraftQualityMin2 = breakpoints.MidCollectability * 10;
                res.CraftQualityMin3 = breakpoints.HighCollectability * 10;
            }
        }
        else if (recipe.RequiredQuality > 0)
        {
            res.CraftQualityMin1 = res.CraftQualityMin2 = res.CraftQualityMin3 = res.CraftQualityMax = (int)recipe.RequiredQuality;
        }

        return res;
    }

    public static void Update()
    {
        // typical craft loop looks like this:
        // 1. starting from IdleNormal state (no condition flags) or IdleBetween state (Crafting + PreparingToCraft condition flags)
        // 2. user presses 'craft' button
        // 2a. craft-start animation starts - this is signified by Crafting40 condition flag, we transition to WaitStart state
        // 2b. quickly after that Crafting flag is set (if it was not already set before)
        // 2c. some time later, animation ends - at this point synth addon is updated and Crafting40 condition flag is cleared - at this point we transition to InProgress state
        // 3. user executes an action that doesn't complete a craft
        // 3a. UseAction hook detects action requests, we save the pending action
        // 3b. on the next frame, client sets Crafting40 condition flag - we transition to WaitAction state (TODO: consider a timeout if flag isn't set, this probably means something went wrong and action was not executed...)
        // 3c. a bit later client receives a bunch of packets: ActorControl (to start animation), StatusEffectList (containing previous statuses and new cp) and UpdateClassInfo (irrelevant)
        // 3d. a few seconds later client receives another bunch of packets: some misc ones, EventPlay64 (contains new crafting state - progress/quality/condition/etc), StatusEffectList (contains new statuses and new cp) and UpdateClassInfo (irrelevant)
        // 3e. on the next frame after receiving EventPlay64, Crafting40 flag is cleared and player is unblocked
        // 3f. sometimes EventPlay64 and final StatusEffectList might end up in a different packet bundle and can get delayed for arbitrary time (and it won't be sent at all if there are no status updates) - we transition back to InProgress state only once statuses are updated
        // 4. user executes an action that completes a craft in any way (success or failure)
        // 4a-d - same as 3a-d
        // 4e. same as 3e, however Crafting40 flag remains set and crafting finish animation starts playing
        // 4f. as soon as we've got fully updated state, we transition to WaitFinish state
        // 4g. some time later, finish animation ends - Crafting40 condition flag is cleared, PreparingToCraft flag is set - at this point we transition to IdleBetween state
        // 5. user exits crafting mode - condition flags are cleared, we transition to IdleNormal state
        // 6. if at some point during craft (InProgress state) user cancels it, we get Crafting40 condition flag without preceeding UseAction - at this point we transition to WaitFinish state
        // since an action can complete a craft only if it increases progress or reduces durability, we can use that to determine when to transition from WaitAction to WaitFinish
        var newState = CurState switch
        {
            State.IdleNormal or State.IdleBetween => TransitionFromIdle(),
            State.WaitStart => TransitionFromWaitStart(),
            State.InProgress => TransitionFromInProgress(),
            State.WaitAction => TransitionFromWaitAction(),
            State.WaitFinish => TransitionFromWaitFinish(),
            _ => TransitionFromInvalid()
        };
        if (newState != CurState)
        {
            Svc.Log.Debug($"Transition: {CurState} -> {newState}");
            CurState = newState;
            StateChanged?.Invoke(newState);
        }
    }

    private static State TransitionFromInvalid()
    {
        if (Svc.Condition[ConditionFlag.Crafting40] || Svc.Condition[ConditionFlag.Crafting] != Svc.Condition[ConditionFlag.PreparingToCraft])
            return State.InvalidState; // stay in this state until we get to one of the idle states

        // wrap up
        if (CurRecipe != null && CurCraft != null && CurStep != null)
            CraftFinished?.Invoke(CurRecipe, CurCraft, CurStep, true); // emulate cancel (TODO reconsider)
        return State.WaitFinish;
    }

    private static State TransitionFromIdle()
    {
        if (Svc.Condition[ConditionFlag.Crafting40])
            return State.WaitStart; // craft started, but we don't yet know details

        if (Svc.Condition[ConditionFlag.PreparingToCraft])
        {
            if (CurState != State.IdleBetween)
            {
                Svc.Log.Error("Unexpected crafting state transition: from idle to preparing");
                return State.InvalidState;
            }
            return State.IdleBetween;
        }

        // stay in default state or exit crafting menu
        return State.IdleNormal;
    }

    private static State TransitionFromWaitStart()
    {
        if (Svc.Condition[ConditionFlag.Crafting40])
            return State.WaitStart; // still waiting

        // note: addon is normally available on the same frame transition ends
        var synthWindow = GetAddon();
        if (synthWindow == null)
        {
            Svc.Log.Error($"Unexpected addon state when craft should've been started");
            return State.WaitStart; // try again next frame
        }

        var itemID = synthWindow->AtkUnitBase.AtkValues[16].UInt;
        var classID = CharacterInfo.JobID - Job.CRP;
        CurRecipe = Svc.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.Recipe>()?.FirstOrDefault(r => r.ItemResult.Row == itemID && r.CraftType.Row == classID);
        if (CurRecipe == null)
        {
            Svc.Log.Error($"Failed to find recipe for {CharacterInfo.JobID} #{itemID}");
            return State.WaitStart; // try again next frame?
        }

        var canHQ = CurRecipe.CanHq;
        CurCraft = BuildCraftStateForRecipe(CurRecipe);
        CurStep = BuildStepState(synthWindow);
        if (CurStep.Index != 1 || CurStep.Condition != Condition.Normal || CurStep.PrevComboAction != Skills.None)
            Svc.Log.Error($"Unexpected initial state: {CurStep}");

        IsTrial = synthWindow->AtkUnitBase.AtkValues[1] is { Type: FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool, Byte: 1 };
        CraftStarted?.Invoke(CurRecipe, CurCraft, CurStep, IsTrial);
        ActionManagerEx.ActionUsed += OnActionUsed;
        return State.InProgress;
    }

    private static State TransitionFromInProgress()
    {
        if (!Svc.Condition[ConditionFlag.Crafting40])
            return State.InProgress; // when either action is executed or craft is cancelled, this condition flag will be set

        if (_pendingAction != Skills.None)
        {
            // we've just tried to execute an action and now got the transition - assume action execution started
            // TODO: consider some timeout? e.g. if action execution did _not_ start for whatever reason, and later user cancelled the craft
            _predictedNextStep = Simulator.Execute(CurCraft!, CurStep!, _pendingAction, 0, 1).Item2;
            return State.WaitAction;
        }
        else
        {
            // transition without action is cancel
            CraftFinished?.Invoke(CurRecipe!, CurCraft!, CurStep!, true);
            return State.WaitFinish; // transition without action is cancel
        }
    }

    private static State TransitionFromWaitAction()
    {
        var synthWindow = GetAddon();
        if (synthWindow == null)
        {
            Svc.Log.Error($"Unexpected addon state when action should've been finished");
            return State.InvalidState;
        }

        var prevIndex = CurStep!.Index;
        if (!Svc.Condition[ConditionFlag.Crafting40])
        {
            // action execution finished, step advanced - make sure all statuses are updated correctly
            var step = BuildStepState(synthWindow);
            if (!StatusesUpdated(step))
                return State.WaitAction;
            var stepIndexIncrement = step.PrevComboAction is Skills.FinalAppraisal or Skills.HeartAndSoul or Skills.CarefulObservation ? 0 : 1;
            if (step.Index != prevIndex + stepIndexIncrement)
                Svc.Log.Error($"Unexpected step index: got {step.Index}, expected {prevIndex}+{stepIndexIncrement} (action={step.PrevComboAction})");
            _pendingAction = Skills.None;
            _predictedNextStep = null;
            CurStep = step;
            CraftAdvanced?.Invoke(CurRecipe!, CurCraft!, CurStep);
            return State.InProgress;
        }
        else if (CurStep!.Progress != GetStepProgress(synthWindow) || CurStep!.Durability != GetStepDurability(synthWindow))
        {
            // action execution finished, craft completes
            var step = BuildStepState(synthWindow);
            if (!StatusesUpdated(step))
                return State.WaitAction;
            if (step.Index != prevIndex)
                Svc.Log.Error($"Unexpected step index: got {step.Index}, expected {prevIndex} (action={step.PrevComboAction})");
            _pendingAction = Skills.None;
            _predictedNextStep = null;
            CurStep = step;
            CraftFinished?.Invoke(CurRecipe!, CurCraft!, CurStep, false);
            return State.WaitFinish;
        }
        else
        {
            // still waiting...
            return State.WaitAction;
        }
    }

    private static State TransitionFromWaitFinish()
    {
        if (Svc.Condition[ConditionFlag.Crafting40])
            return State.WaitFinish; // transition still in progress

        ActionManagerEx.ActionUsed -= OnActionUsed;
        _pendingAction = Skills.None;
        _predictedNextStep = null;
        CurRecipe = null;
        CurCraft = null;
        CurStep = null;
        IsTrial = false;
        return Svc.Condition[ConditionFlag.PreparingToCraft] ? State.IdleBetween : State.IdleNormal;
    }

    private static AddonSynthesis* GetAddon()
    {
        var synthWindow = (AddonSynthesis*)Svc.GameGui.GetAddonByName("Synthesis");
        if (synthWindow == null)
            return null; // not ready

        if (synthWindow->AtkUnitBase.AtkValuesCount < 26)
        {
            Svc.Log.Error($"Unexpected addon state: 0x{(nint)synthWindow:X} {synthWindow->AtkUnitBase.AtkValuesCount} {synthWindow->AtkUnitBase.UldManager.NodeListCount})");
            return null;
        }

        return synthWindow;
    }

    private static int GetStepIndex(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[15].Int;
    private static int GetStepProgress(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[5].Int;
    private static int GetStepQuality(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[9].Int;
    private static int GetStepDurability(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[7].Int;
    private static Condition GetStepCondition(AddonSynthesis* synthWindow) => (Condition)synthWindow->AtkUnitBase.AtkValues[12].Int;

    private static StepState BuildStepState(AddonSynthesis* synthWindow) => new ()
    {
        Index = GetStepIndex(synthWindow),
        Progress = GetStepProgress(synthWindow),
        Quality = GetStepQuality(synthWindow),
        Durability = GetStepDurability(synthWindow),
        RemainingCP = (int)CharacterInfo.CurrentCP,
        Condition = GetStepCondition(synthWindow),
        IQStacks = GetStatus(Buffs.InnerQuiet)?.Param ?? 0,
        WasteNotLeft = GetStatus(Buffs.WasteNot2)?.Param ?? GetStatus(Buffs.WasteNot)?.Param ?? 0,
        ManipulationLeft = GetStatus(Buffs.Manipulation)?.Param ?? 0,
        GreatStridesLeft = GetStatus(Buffs.GreatStrides)?.Param ?? 0,
        InnovationLeft = GetStatus(Buffs.Innovation)?.Param ?? 0,
        VenerationLeft = GetStatus(Buffs.Veneration)?.Param ?? 0,
        MuscleMemoryLeft = GetStatus(Buffs.MuscleMemory)?.Param ?? 0,
        FinalAppraisalLeft = GetStatus(Buffs.FinalAppraisal)?.Param ?? 0,
        CarefulObservationLeft = P.Config.UseSpecialist && ActionManagerEx.CanUseSkill(Skills.CarefulObservation) ? 1 : 0,
        HeartAndSoulActive = GetStatus(Buffs.HeartAndSoul) != null,
        HeartAndSoulAvailable = P.Config.UseSpecialist && ActionManagerEx.CanUseSkill(Skills.HeartAndSoul),
        PrevComboAction = _pendingAction,
    };

    private static bool StatusesEqual(StepState l, StepState r) =>
        (l.IQStacks, l.WasteNotLeft, l.ManipulationLeft, l.GreatStridesLeft, l.InnovationLeft, l.VenerationLeft, l.MuscleMemoryLeft, l.FinalAppraisalLeft, l.HeartAndSoulActive) ==
        (r.IQStacks, r.WasteNotLeft, r.ManipulationLeft, r.GreatStridesLeft, r.InnovationLeft, r.VenerationLeft, r.MuscleMemoryLeft, r.FinalAppraisalLeft, r.HeartAndSoulActive);

    private static bool StatusesUpdated(StepState step)
    {
        // this is a bit of a hack to work around for statuses being updated by a packet that arrives after EventPlay64, which updates the actual crafting state
        if (StatusesEqual(step, _predictedNextStep!))
            return true; // all good, updated
        if (StatusesEqual(step, CurStep!))
        {
            Svc.Log.Debug("Waiting for status update...");
            return false; // still old ones, waiting...
        }
        // ok, so statuses have changed, just not in the way we expect - complain and consider them to be updated
        Svc.Log.Error($"Unexpected status update: had {CurStep}, expected {_predictedNextStep}, got {step} - probably there's a bug in simulator");
        return true;
    }

    private static void OnActionUsed(Skills action) => _pendingAction = action;

    private static Dalamud.Game.ClientState.Statuses.Status? GetStatus(uint statusID) => Svc.ClientState.LocalPlayer?.StatusList.FirstOrDefault(s => s.StatusId == statusID);
}
