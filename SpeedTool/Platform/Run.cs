using SpeedTool.Splits;
using SpeedTool.Timer;
using SpeedTool.Util;

namespace SpeedTool.Platform;

public class Run : ISplitsSource
{
    public Run(Game game, Split[] splits, RunInfo? comparisonRun)
    {
        this.splits = splits;
        this.game = game;
        FlattenSplits();

        if(comparisonRun != null && comparisonRun.Splits.Length == flattened.Length)
            comparison = comparisonRun;
    }

    public bool Started { get; private set; }
    public bool IsFinished { get; private set; }

    public ITimerSource Timer
    {
        get
        {
            return timer;
        }
    }

    public SplitDisplayInfo CurrentSplit => flattened[currentSplit];

    public void Split()
    {
        // No splitting when paused!
        if(timer.CurrentState == TimerState.Paused)
            return;

        if(!Started)
        {
            if(timer.CurrentState != TimerState.NoState)
            {
                Platform.SharedPlatform.ReloadRun();
                timer.Reset();
                currentSplit = 0;
                IsFinished = false;
                return;
            }
            Started = true;
            currentSplit = -1;
            NextSplit();
            flattened[currentSplit].IsCurrent = true;
            timer.Reset();
            timer.Start();
            return;
        }

        NextSplit();
        if(currentSplit >= flattened.Length)
        {
            timer.Stop();
            Started = false;
            IsFinished = true;
            currentSplit--;
            SaveRun();
            return;
        }
        flattened[currentSplit].IsCurrent = true;
    }

    public void Pause()
    {
        if(Started)
            timer.Pause();
    }

    public void SkipSplit()
    {
        if(!Started)
        {
            return;
        }

        flattened[currentSplit].IsCurrent = false;
        NextSplitNoUpdate();
        if(currentSplit >= flattened.Length)
        {
            timer.Stop();
            Started = false;
            currentSplit--;
            return;
        }
        flattened[currentSplit].IsCurrent = true;
    }

    private void NextSplitNoUpdate()
    {
        // Write split time
        currentSplit++;

        // Roll over parent splitts
        while(infoStack.Count > 0 && infoStack.Peek().Level >= flattened[currentSplit].Level)
        {
            var p = infoStack.Pop();
        }

        // Roll over to the first actual split in the tree
        while(NextFlatSplit != null && CurrentFlatSplit.Level < NextFlatSplit.Value.Level)
        {
            infoStack.Push(flattened[currentSplit]);
            currentSplit++;
        }

        // If split has subsplits, go to next split
        if(NextFlatSplit != null && NextFlatSplit.Value.Level > CurrentFlatSplit.Level)
        {
            SkipSplit();
        }
    }

    private void FlattenSplits()
    {
        List<SplitDisplayInfo> f = new();
        List<int> zero = new();
        foreach(var split in splits)
        {
            zero.Add(f.Count);
            f.AddRange(split.Flatten());
        }

        flattened = f.ToArray();
        zeroLevelSplits = zero.ToArray();
    }

    int currentSplit = 0;

    private Split[] splits;

    private SplitDisplayInfo[] flattened = [];
    private int[] zeroLevelSplits = [];

    public IEnumerable<SplitDisplayInfo> GetSplits(int count)
    {
        // Honestly, I wrote this function when I was high on sleep deprevation and eye disease,
        // so it might be difficult to understand the hell is going on here.
        // I'm trying to explain it as an aftermath with clear head, so don't mind me

        // This function has a `wieght` parameter to decide how to value splits from each side.
        // TODO: This weight feature doesn't really work well, it should probably be changed to something else
        var currentLevelSplits = GetCurrentLevelSplits();
        var posInCurrentLevel = currentLevelSplits.IndexOf(CurrentFlatSplit);

        // If on level 0, just get enough splits to display
        if(CurrentFlatSplit.Level == 0)
        {
            return currentLevelSplits.TakeAtPosWeighted(count, posInCurrentLevel, weight);
        }

        // Always display splits tree on top
        var topmostSplits = GetTopmostSplits();
        var topmostCount = Math.Min(count - 1, topmostSplits.Count);

        // If we have enough tree to fill in the requested space, do that
        if(topmostCount == count - 1)
            return topmostSplits.TakeLast(topmostCount).Append(CurrentFlatSplit);

        var currentLevelCount = Math.Min(currentLevelSplits.Count, count - topmostCount);

        // If tree + current level fits the space, do that
        if(currentLevelCount + topmostCount >= count)
        {
            return topmostSplits.TakeLast(topmostCount).Concat(currentLevelSplits.TakeAtPosWeighted(currentLevelCount, posInCurrentLevel, weight));
        }

        var middle = topmostSplits.TakeLast(topmostCount).Concat(currentLevelSplits.TakeAtPosWeighted(currentLevelCount, posInCurrentLevel, weight));

        var zeroLevelCount = count - currentLevelCount - topmostCount;

        zeroLevelSplits.Select(x => flattened[x]).TakeAtPosWeighted(zeroLevelCount, 1, weight);

        // Figure out next and previous splits to fit the space
        List<SplitDisplayInfo> prev = new();
        List<SplitDisplayInfo> next = new();
        int i = 0;
        while(i < zeroLevelSplits.Length && zeroLevelSplits[i] < currentSplit)
        {
            if(zeroLevelSplits[i] == currentSplit)
            {
                i++;
                continue;
            }
            prev.Add(flattened[zeroLevelSplits[i]]);
            i++;
        }
        prev = prev.Take(prev.Count - 1).ToList();
        while(i < zeroLevelSplits.Length)
        {
            if(zeroLevelSplits[i] == currentSplit)
            {
                i++;
                continue;
            }
            next.Add(flattened[zeroLevelSplits[i]]);
            i++;
        }

        // Figure out how many splits to take from the left and from the right
        var wantRight = (int)(weight / 100.0 * zeroLevelCount);
        wantRight = Math.Min(next.Count, wantRight);

        var wantLeft = zeroLevelCount - wantRight;
        wantLeft = Math.Min(wantLeft, prev.Count);

        return prev.TakeLast(wantLeft).Concat(middle).Concat(next.Take(wantRight));
    }

    private void NextSplit()
    {
        // Write split time
        if(currentSplit >= 0)
        {
            flattened[currentSplit].IsCurrent = false;
            for(int i = 0; i < (int)TimingMethod.Last; i++)
            {
                var tm = (TimingMethod)i; 
                flattened[currentSplit].Times[tm] = Platform.SharedPlatform.GetTimerFor(tm).CurrentTime;
            }
            if(comparison != null)
                flattened[currentSplit].DeltaTimes = flattened[currentSplit].Times - comparison.Splits[currentSplit].Times;
        }

        currentSplit++;

        // Roll over parent splitts and write times for them
        while(infoStack.Count > 0 && infoStack.Peek().Level >= flattened[currentSplit].Level)
        {
            var p = infoStack.Pop();
            for(int i = 0; i < (int)TimingMethod.Last; i++)
            {
                var tm = (TimingMethod)i; 
                p.Times[tm] = Platform.SharedPlatform.GetTimerFor(tm).CurrentTime;
            }
            if(comparison != null)
                p.DeltaTimes = flattened[currentSplit].Times - comparison.Splits[currentSplit].Times;
        }

        // Roll over to the first actual split in the tree
        while(NextFlatSplit != null && CurrentFlatSplit.Level < NextFlatSplit.Value.Level)
        {
            infoStack.Push(flattened[currentSplit]);
            currentSplit++;
        }

        // If split has subsplits, go to next split
        if(NextFlatSplit != null && NextFlatSplit.Value.Level > CurrentFlatSplit.Level)
        {
            NextSplit();
        }
    }

    private List<SplitDisplayInfo> GetCurrentLevelSplits()
    {
        var begin = FirstSubsplitPos();
        var end = LastSubsplitPos();

        List<SplitDisplayInfo> infos = new();

        for(int i = begin; i <= end; i++)
        {
            if(flattened[i].Level == CurrentFlatSplit.Level)
                infos.Add(flattened[i]);
        }

        return infos;
    }

    private List<SplitDisplayInfo> GetTopmostSplits()
    {
        var list = infoStack.ToList();
        list.Reverse();
        return list;
    }

    private int FirstSubsplitPos()
    {
        for(int i = currentSplit; i >= 0; i--)
        {
            if(flattened[i].Level < CurrentFlatSplit.Level)
                return i + 1;
        }
        return 0;
    }

    private int LastSubsplitPos()
    {
        for(int i = currentSplit; i < flattened.Length; i++)
        {
            if(flattened[i].Level < CurrentFlatSplit.Level)
                return i - 1;
        }
        return flattened.Length - 1;
    }

    public RunInfo GetRunInfo()
    {
        if(game == null)
            return new RunInfo("unnamed", "unnamed", flattened.Last().Times, flattened);
        return new RunInfo(game!.Name, Platform.SharedPlatform.CurrentCategory!.Name, flattened.Last().Times, flattened);
    }

    private void SaveRun()
    {
        var tm = game.DefaultTimingMethod;
        if(comparison == null || flattened.Last().Times[tm] < comparison!.Times[tm])
        {
            Platform.SharedPlatform.SaveRunAsPB(GetRunInfo());
        }
    }

    public void UndoSplit()
    {
        while(PreviousFlatSplit != null)
        {
            flattened[currentSplit].IsCurrent = false;
            currentSplit--;
            flattened[currentSplit].Times.Reset();
            flattened[currentSplit].DeltaTimes.Reset();

            if(NextFlatSplit!.Value.Level <= CurrentFlatSplit.Level)
            {
                break;
            }
        }
        flattened[currentSplit].IsCurrent = true;
        ResetInfoStack();

        // Reset infoStack somehow
        foreach(var i in infoStack.AsEnumerable())
        {
            i.Times.Reset();
            i.DeltaTimes.Reset();
        }
    }

    private void ResetInfoStack()
    {
        var upTo = currentSplit;
        currentSplit = 0;
        infoStack = new();
        while(currentSplit != upTo)
        {
            NextSplitNoUpdate();
        }
    }

    private RunInfo? comparison;
    private Game game;

    private Stack<SplitDisplayInfo> infoStack = new();

    private SplitDisplayInfo? NextFlatSplit => currentSplit >= flattened.Length - 1 ? null : flattened[currentSplit + 1];
    private SplitDisplayInfo CurrentFlatSplit => flattened[currentSplit];
    private SplitDisplayInfo? PreviousFlatSplit => currentSplit <= 0 ? null : flattened[currentSplit - 1];

    public SplitDisplayInfo? PreviousSplit
    {
        get
        {
            if(PreviousFlatSplit == null)
                return null;
            
            int i = currentSplit;
            while(i > 0)
            {
                i--;

                if(flattened[i + 1].Level <= flattened[i].Level)
                {
                    break;
                }
            }

            if(i >= 0)
                return flattened[i];

            return null;
        }
    }

    const int weight = 75;

    BasicTimer timer = new();
}
