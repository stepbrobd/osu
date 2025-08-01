﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Pooling;
using osu.Framework.Threading;
using osu.Framework.Utils;
using osu.Game.Beatmaps;
using osu.Game.Collections;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.Carousel;
using osu.Game.Graphics.UserInterface;
using osu.Game.Screens.Select;

namespace osu.Game.Screens.SelectV2
{
    [Cached]
    public partial class BeatmapCarousel : Carousel<BeatmapInfo>
    {
        public Action<BeatmapInfo>? RequestPresentBeatmap { private get; init; }

        /// <summary>
        /// From the provided beatmaps, select the most appropriate one for the user's skill.
        /// </summary>
        public required Action<IEnumerable<BeatmapInfo>> RequestRecommendedSelection { private get; init; }

        /// <summary>
        /// Selection requested for the provided beatmap.
        /// </summary>
        public required Action<BeatmapInfo> RequestSelection { private get; init; }

        public const float SPACING = 3f;

        private IBindableList<BeatmapSetInfo> detachedBeatmaps = null!;

        private readonly LoadingLayer loading;

        private readonly BeatmapCarouselFilterMatching matching;
        private readonly BeatmapCarouselFilterGrouping grouping;

        /// <summary>
        /// Total number of beatmap difficulties displayed with the filter.
        /// </summary>
        public int MatchedBeatmapsCount => matching.BeatmapItemsCount;

        protected override float GetSpacingBetweenPanels(CarouselItem top, CarouselItem bottom)
        {
            // Group panels do not overlap with any other panel but should overlap with themselves.
            if ((top.Model is GroupDefinition) ^ (bottom.Model is GroupDefinition))
                return SPACING * 2;

            if (grouping.BeatmapSetsGroupedTogether)
            {
                // Give some space around the expanded beatmap set, at the top..
                if (bottom.Model is BeatmapSetInfo && bottom.IsExpanded)
                    return SPACING * 2;

                // ..and the bottom.
                if (top.Model is BeatmapInfo && bottom.Model is BeatmapSetInfo)
                    return SPACING * 2;

                // Beatmap difficulty panels do not overlap with themselves or any other panel.
                if (top.Model is BeatmapInfo || bottom.Model is BeatmapInfo)
                    return SPACING;
            }
            else
            {
                // `CurrentSelectionItem` cannot be used here because it may not be correctly set yet.
                if (CurrentSelection != null && (CheckModelEquality(top.Model, CurrentSelection) || CheckModelEquality(bottom.Model, CurrentSelection)))
                    return SPACING * 2;
            }

            return -SPACING;
        }

        public BeatmapCarousel()
        {
            DebounceDelay = 100;
            DistanceOffscreenToPreload = 100;

            // Account for the osu! logo being in the way.
            Scroll.ScrollbarPaddingBottom = 70;

            Filters = new ICarouselFilter[]
            {
                matching = new BeatmapCarouselFilterMatching(() => Criteria!),
                new BeatmapCarouselFilterSorting(() => Criteria!),
                grouping = new BeatmapCarouselFilterGrouping(() => Criteria!, () => detachedCollections())
            };

            AddInternal(loading = new LoadingLayer());
        }

        [BackgroundDependencyLoader]
        private void load(BeatmapStore beatmapStore, RealmAccess realm, AudioManager audio, OsuConfigManager config, CancellationToken? cancellationToken)
        {
            setupPools();
            detachedBeatmaps = beatmapStore.GetBeatmapSets(cancellationToken);
            detachedCollections = () => realm.Run(r => r.All<BeatmapCollection>().AsEnumerable().Detach());
            loadSamples(audio);

            config.BindWith(OsuSetting.RandomSelectAlgorithm, randomAlgorithm);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            detachedBeatmaps.BindCollectionChanged(beatmapSetsChanged, true);
        }

        #region Beatmap source hookup

        private void beatmapSetsChanged(object? beatmaps, NotifyCollectionChangedEventArgs changed) => Schedule(() =>
        {
            // This callback is scheduled to ensure there's no added overhead during gameplay.
            // If this ever becomes an issue, it's important to note that the actual carousel filtering is already
            // implemented in a way it will only run when at song select.
            //
            // The overhead we are avoiding here is that of this method directly – things like Items.IndexOf calls
            // that can be slow for very large beatmap libraries. There are definitely ways to optimise this further.

            // TODO: moving management of BeatmapInfo tracking to BeatmapStore might be something we want to consider.
            // right now we are managing this locally which is a bit of added overhead.
            IEnumerable<BeatmapSetInfo>? newItems = changed.NewItems?.Cast<BeatmapSetInfo>();
            IEnumerable<BeatmapSetInfo>? oldItems = changed.OldItems?.Cast<BeatmapSetInfo>();

            switch (changed.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    Items.AddRange(newItems!.SelectMany(s => s.Beatmaps));
                    break;

                case NotifyCollectionChangedAction.Remove:
                    bool selectedSetDeleted = false;

                    foreach (var set in oldItems!)
                    {
                        foreach (var beatmap in set.Beatmaps)
                        {
                            Items.RemoveAll(i => i is BeatmapInfo bi && beatmap.Equals(bi));
                            selectedSetDeleted |= CheckModelEquality(CurrentSelection, beatmap);
                        }
                    }

                    // After removing all items in this batch, we want to make an immediate reselection
                    // based on adjacency to the previous selection if it was deleted.
                    //
                    // This needs to be done immediately to avoid song select making a random selection.
                    // This needs to be done in this class because we need to know final display order.
                    // This needs to be done with attention to detail of which beatmaps have not been deleted.
                    if (selectedSetDeleted && CurrentSelectionIndex != null)
                    {
                        var items = GetCarouselItems()!;
                        if (items.Count == 0)
                            break;

                        bool success = false;

                        // Try selecting forwards first
                        for (int i = CurrentSelectionIndex.Value + 1; i < items.Count; i++)
                        {
                            if (attemptSelection(items[i]))
                            {
                                success = true;
                                break;
                            }
                        }

                        if (success)
                            break;

                        // Then try backwards (we might be at the end of available items).
                        for (int i = Math.Min(items.Count - 1, CurrentSelectionIndex.Value); i >= 0; i--)
                        {
                            if (attemptSelection(items[i]))
                                break;
                        }

                        bool attemptSelection(CarouselItem item)
                        {
                            if (CheckValidForSetSelection(item))
                            {
                                if (item.Model is BeatmapInfo beatmapInfo)
                                {
                                    // check the new selection wasn't deleted above
                                    if (!Items.Contains(beatmapInfo))
                                        return false;

                                    RequestSelection(beatmapInfo);
                                    return true;
                                }

                                if (item.Model is BeatmapSetInfo beatmapSetInfo)
                                {
                                    if (oldItems.Contains(beatmapSetInfo))
                                        return false;

                                    RequestRecommendedSelection(beatmapSetInfo.Beatmaps);
                                    return true;
                                }
                            }

                            return false;
                        }
                    }

                    break;

                case NotifyCollectionChangedAction.Move:
                    // We can ignore move operations as we are applying our own sort in all cases.
                    break;

                case NotifyCollectionChangedAction.Replace:
                    var oldSetBeatmaps = oldItems!.Single().Beatmaps;
                    var newSetBeatmaps = newItems!.Single().Beatmaps.ToList();

                    // Handling replace operations is a touch manual, as we need to locally diff the beatmaps of each version of the beatmap set.
                    // Matching is done based on online IDs, then difficulty names as these are the most stable thing between updates (which are usually triggered
                    // by users editing the beatmap or by difficulty/metadata recomputation).
                    //
                    // In the case of difficulty reprocessing, this will trigger multiple times per beatmap as it's always triggering a set update.
                    // We may want to look to improve this in the future either here or at the source (only trigger an update after all difficulties
                    // have been processed) if it becomes an issue for animation or performance reasons.
                    foreach (var beatmap in oldSetBeatmaps)
                    {
                        int previousIndex = Items.IndexOf(beatmap);
                        Debug.Assert(previousIndex >= 0);

                        BeatmapInfo? matchingNewBeatmap =
                            newSetBeatmaps.SingleOrDefault(b => b.OnlineID > 0 && b.OnlineID == beatmap.OnlineID) ??
                            newSetBeatmaps.SingleOrDefault(b => b.DifficultyName == beatmap.DifficultyName && b.Ruleset.Equals(beatmap.Ruleset));

                        if (matchingNewBeatmap != null)
                        {
                            // TODO: should this exist in song select instead of here?
                            // we need to ensure the global beatmap is also updated alongside changes.
                            if (CurrentSelection != null && CheckModelEquality(beatmap, CurrentSelection))
                                RequestSelection(matchingNewBeatmap);

                            Items.ReplaceRange(previousIndex, 1, [matchingNewBeatmap]);
                            newSetBeatmaps.Remove(matchingNewBeatmap);
                        }
                        else
                        {
                            Items.RemoveAt(previousIndex);
                        }
                    }

                    // Add any items which weren't found in the previous pass (difficulty names didn't match).
                    foreach (var beatmap in newSetBeatmaps)
                        Items.Add(beatmap);

                    break;

                case NotifyCollectionChangedAction.Reset:
                    Items.Clear();
                    break;
            }
        });

        #endregion

        #region Selection handling

        protected GroupDefinition? ExpandedGroup { get; private set; }

        protected BeatmapSetInfo? ExpandedBeatmapSet { get; private set; }

        protected override bool ShouldActivateOnKeyboardSelection(CarouselItem item) =>
            grouping.BeatmapSetsGroupedTogether && item.Model is BeatmapInfo;

        protected override void HandleItemActivated(CarouselItem item)
        {
            try
            {
                switch (item.Model)
                {
                    case GroupDefinition group:
                        // Special case – collapsing an open group.
                        if (ExpandedGroup == group)
                        {
                            setExpansionStateOfGroup(ExpandedGroup, false);
                            ExpandedGroup = null;
                            return;
                        }

                        setExpandedGroup(group);

                        // If the active selection is within this group, it should get keyboard focus immediately.
                        if (CurrentSelectionItem?.IsVisible == true && CurrentSelection is BeatmapInfo info)
                            RequestSelection(info);

                        return;

                    case BeatmapSetInfo setInfo:
                        selectRecommendedDifficultyForBeatmapSet(setInfo);
                        return;

                    case BeatmapInfo beatmapInfo:
                        if (CurrentSelection != null && CheckModelEquality(CurrentSelection, beatmapInfo))
                        {
                            RequestPresentBeatmap?.Invoke(beatmapInfo);
                            return;
                        }

                        RequestSelection(beatmapInfo);
                        return;
                }
            }
            finally
            {
                playActivationSound(item);
            }
        }

        protected override void HandleItemSelected(object? model)
        {
            base.HandleItemSelected(model);

            switch (model)
            {
                case BeatmapSetInfo:
                case GroupDefinition:
                    throw new InvalidOperationException("Groups should never become selected");

                case BeatmapInfo beatmapInfo:
                    // Find any containing group. There should never be too many groups so iterating is efficient enough.
                    GroupDefinition? containingGroup = grouping.GroupItems.SingleOrDefault(kvp => kvp.Value.Any(i => CheckModelEquality(i.Model, beatmapInfo))).Key;

                    setExpandedGroup(containingGroup);

                    if (grouping.BeatmapSetsGroupedTogether)
                        setExpandedSet(beatmapInfo);
                    break;
            }
        }

        protected override void HandleFilterCompleted()
        {
            base.HandleFilterCompleted();

            attemptSelectSingleFilteredResult();

            // Store selected group before handling selection (it may implicitly change the expanded group).
            var groupForReselection = ExpandedGroup;

            // Ensure correct post-selection logic is handled on the new items list.
            // This will update the visual state of the selected item.
            HandleItemSelected(CurrentSelection);

            // If a group was selected that is not the one containing the selection, attempt to reselect it.
            // If the original group was not found, ExpandedGroup will already have been updated to a valid value in `HandleItemSelected` above.
            if (groupForReselection != null && grouping.GroupItems.TryGetValue(groupForReselection, out _))
                setExpandedGroup(groupForReselection);
        }

        private void selectRecommendedDifficultyForBeatmapSet(BeatmapSetInfo beatmapSet)
        {
            // Selecting a set isn't valid – let's re-select the first visible difficulty.
            if (grouping.SetItems.TryGetValue(beatmapSet, out var items))
            {
                var beatmaps = items.Select(i => i.Model).OfType<BeatmapInfo>();
                RequestRecommendedSelection(beatmaps);
            }
        }

        /// <summary>
        /// If we don't have a selection and there's a single beatmap set returned, select it for the user.
        /// </summary>
        private void attemptSelectSingleFilteredResult()
        {
            var items = GetCarouselItems();

            if (items == null || items.Count == 0) return;

            BeatmapSetInfo? beatmapSetInfo = null;

            foreach (var item in items)
            {
                if (item.Model is BeatmapInfo beatmapInfo)
                {
                    if (beatmapSetInfo == null)
                    {
                        beatmapSetInfo = beatmapInfo.BeatmapSet!;
                        continue;
                    }

                    // Found a beatmap with a different beatmap set, abort.
                    if (!beatmapSetInfo.Equals(beatmapInfo.BeatmapSet))
                        return;
                }
            }

            var beatmaps = items.Select(i => i.Model).OfType<BeatmapInfo>();

            if (beatmaps.Any(b => b.Equals(CurrentSelection as BeatmapInfo)))
                return;

            RequestRecommendedSelection(beatmaps);
        }

        protected override bool CheckValidForGroupSelection(CarouselItem item) => item.Model is GroupDefinition;

        protected override bool CheckValidForSetSelection(CarouselItem item)
        {
            switch (item.Model)
            {
                case BeatmapSetInfo:
                    return true;

                case BeatmapInfo:
                    return !grouping.BeatmapSetsGroupedTogether;

                case GroupDefinition:
                    return false;

                default:
                    throw new ArgumentException($"Unsupported model type {item.Model}");
            }
        }

        private void setExpandedGroup(GroupDefinition group)
        {
            if (ExpandedGroup != null)
                setExpansionStateOfGroup(ExpandedGroup, false);

            ExpandedGroup = group;

            if (ExpandedGroup != null)
                setExpansionStateOfGroup(group, true);
        }

        private void setExpansionStateOfGroup(GroupDefinition group, bool expanded)
        {
            if (grouping.GroupItems.TryGetValue(group, out var items))
            {
                if (expanded)
                {
                    foreach (var i in items)
                    {
                        switch (i.Model)
                        {
                            case GroupDefinition:
                                i.IsExpanded = true;
                                break;

                            case BeatmapSetInfo set:
                                // Case where there are set headers, header should be visible
                                // and items should use the set's expanded state.
                                i.IsVisible = true;
                                setExpansionStateOfSetItems(set, i.IsExpanded);
                                break;

                            default:
                                // Case where there are no set headers, all items should be visible.
                                if (!grouping.BeatmapSetsGroupedTogether)
                                    i.IsVisible = true;
                                break;
                        }
                    }
                }
                else
                {
                    foreach (var i in items)
                    {
                        switch (i.Model)
                        {
                            case GroupDefinition:
                                i.IsExpanded = false;
                                break;

                            default:
                                i.IsVisible = false;
                                break;
                        }
                    }
                }
            }
        }

        private void setExpandedSet(BeatmapInfo beatmapInfo)
        {
            if (ExpandedBeatmapSet != null)
                setExpansionStateOfSetItems(ExpandedBeatmapSet, false);
            ExpandedBeatmapSet = beatmapInfo.BeatmapSet!;
            setExpansionStateOfSetItems(ExpandedBeatmapSet, true);
        }

        private void setExpansionStateOfSetItems(BeatmapSetInfo set, bool expanded)
        {
            if (grouping.SetItems.TryGetValue(set, out var items))
            {
                foreach (var i in items)
                {
                    if (i.Model is BeatmapSetInfo)
                        i.IsExpanded = expanded;
                    else
                        i.IsVisible = expanded;
                }
            }
        }

        #endregion

        #region Audio

        private Sample? sampleChangeDifficulty;
        private Sample? sampleChangeSet;
        private Sample? sampleToggleGroup;

        private double audioFeedbackLastPlaybackTime;

        private void loadSamples(AudioManager audio)
        {
            sampleChangeDifficulty = audio.Samples.Get(@"SongSelect/select-difficulty");
            sampleChangeSet = audio.Samples.Get(@"SongSelect/select-expand");
            sampleToggleGroup = audio.Samples.Get(@"SongSelect/select-group");

            spinSample = audio.Samples.Get("SongSelect/random-spin");
            randomSelectSample = audio.Samples.Get(@"SongSelect/select-random");
        }

        private void playActivationSound(CarouselItem item)
        {
            if (Time.Current - audioFeedbackLastPlaybackTime >= OsuGameBase.SAMPLE_DEBOUNCE_TIME)
            {
                switch (item.Model)
                {
                    case GroupDefinition:
                        sampleToggleGroup?.Play();
                        return;

                    case BeatmapSetInfo:
                        sampleChangeSet?.Play();
                        return;

                    case BeatmapInfo:
                        sampleChangeDifficulty?.Play();
                        return;
                }

                audioFeedbackLastPlaybackTime = Time.Current;
            }
        }

        #endregion

        #region Animation

        /// <summary>
        /// Moves non-selected beatmaps to the right, hiding off-screen.
        /// </summary>
        public bool VisuallyFocusSelected { get; set; }

        private float selectionFocusOffset;

        protected override void Update()
        {
            base.Update();

            selectionFocusOffset = (float)Interpolation.DampContinuously(selectionFocusOffset, VisuallyFocusSelected ? 300 : 0, 100, Time.Elapsed);
        }

        protected override float GetPanelXOffset(Drawable panel)
        {
            return base.GetPanelXOffset(panel) + (((ICarouselPanel)panel).Selected.Value ? 0 : selectionFocusOffset);
        }

        #endregion

        #region Filtering

        public FilterCriteria? Criteria { get; private set; }

        private ScheduledDelegate? loadingDebounce;

        public void Filter(FilterCriteria criteria, bool showLoadingImmediately = false)
        {
            bool resetDisplay = grouping.BeatmapSetsGroupedTogether != BeatmapCarouselFilterGrouping.ShouldGroupBeatmapsTogether(criteria);

            Criteria = criteria;

            loadingDebounce ??= Scheduler.AddDelayed(() =>
            {
                if (loading.State.Value == Visibility.Visible)
                    return;

                Scroll.FadeColour(OsuColour.Gray(0.5f), 1000, Easing.OutQuint);
                loading.Show();
            }, showLoadingImmediately ? 0 : 250);

            FilterAsync(resetDisplay).ContinueWith(_ => Schedule(() =>
            {
                loadingDebounce?.Cancel();
                loadingDebounce = null;

                Scroll.FadeColour(OsuColour.Gray(1f), 500, Easing.OutQuint);
                loading.Hide();
            }));
        }

        protected override Task<IEnumerable<CarouselItem>> FilterAsync(bool clearExistingPanels = false)
        {
            if (Criteria == null)
                return Task.FromResult(Enumerable.Empty<CarouselItem>());

            return base.FilterAsync(clearExistingPanels);
        }

        #endregion

        #region Drawable pooling

        private readonly DrawablePool<PanelBeatmap> beatmapPanelPool = new DrawablePool<PanelBeatmap>(100);
        private readonly DrawablePool<PanelBeatmapStandalone> standalonePanelPool = new DrawablePool<PanelBeatmapStandalone>(100);
        private readonly DrawablePool<PanelBeatmapSet> setPanelPool = new DrawablePool<PanelBeatmapSet>(100);
        private readonly DrawablePool<PanelGroup> groupPanelPool = new DrawablePool<PanelGroup>(100);
        private readonly DrawablePool<PanelGroupStarDifficulty> starsGroupPanelPool = new DrawablePool<PanelGroupStarDifficulty>(11);

        private void setupPools()
        {
            AddInternal(starsGroupPanelPool);
            AddInternal(groupPanelPool);
            AddInternal(beatmapPanelPool);
            AddInternal(standalonePanelPool);
            AddInternal(setPanelPool);
        }

        protected override bool CheckModelEquality(object? x, object? y)
        {
            // In the confines of the carousel logic, we assume that CurrentSelection (and all items) are using non-stale
            // BeatmapInfo reference, and that we can match based on beatmap / beatmapset (GU)IDs.
            //
            // If there's a case where updates don't come in as expected, diagnosis should start from BeatmapStore, ensuring
            // it is doing a Replace operation on the list. If it is, then check the local handling in beatmapSetsChanged
            // before changing matching requirements here.

            if (x is BeatmapSetInfo beatmapSetX && y is BeatmapSetInfo beatmapSetY)
                return beatmapSetX.Equals(beatmapSetY);

            if (x is BeatmapInfo beatmapX && y is BeatmapInfo beatmapY)
                return beatmapX.Equals(beatmapY);

            if (x is GroupDefinition groupX && y is GroupDefinition groupY)
                return groupX.Equals(groupY);

            if (x is StarDifficultyGroupDefinition starX && y is StarDifficultyGroupDefinition starY)
                return starX.Equals(starY);

            return base.CheckModelEquality(x, y);
        }

        protected override Drawable GetDrawableForDisplay(CarouselItem item)
        {
            switch (item.Model)
            {
                case StarDifficultyGroupDefinition:
                    return starsGroupPanelPool.Get();

                case GroupDefinition:
                    return groupPanelPool.Get();

                case BeatmapInfo:
                    if (!grouping.BeatmapSetsGroupedTogether)
                        return standalonePanelPool.Get();

                    return beatmapPanelPool.Get();

                case BeatmapSetInfo:
                    return setPanelPool.Get();
            }

            throw new InvalidOperationException();
        }

        #endregion

        #region Random selection handling

        private readonly Bindable<RandomSelectAlgorithm> randomAlgorithm = new Bindable<RandomSelectAlgorithm>();
        private readonly List<BeatmapInfo> previouslyVisitedRandomBeatmaps = new List<BeatmapInfo>();
        private readonly List<BeatmapInfo> randomHistory = new List<BeatmapInfo>();

        private Sample? spinSample;
        private Sample? randomSelectSample;

        private Func<List<BeatmapCollection>> detachedCollections = null!;

        public bool NextRandom()
        {
            var carouselItems = GetCarouselItems();

            if (carouselItems?.Any() != true)
                return false;

            var selectionBefore = CurrentSelectionItem;
            var beatmapBefore = selectionBefore?.Model as BeatmapInfo;

            bool success;

            if (beatmapBefore != null)
            {
                // keep track of visited beatmaps and sets for rewind
                randomHistory.Add(beatmapBefore);
                // keep track of visited beatmaps for "RandomPermutation" random tracking.
                // note that this is reset when we run out of beatmaps, while `randomHistory` is not.
                previouslyVisitedRandomBeatmaps.Add(beatmapBefore);
            }

            if (grouping.BeatmapSetsGroupedTogether)
                success = nextRandomSet();
            else
                success = nextRandomBeatmap();

            if (!success)
            {
                if (beatmapBefore != null)
                    randomHistory.RemoveAt(randomHistory.Count - 1);
                return false;
            }

            // CurrentSelectionItem won't be valid until UpdateAfterChildren.
            // We probably want to fix this at some point since a few places are working-around this quirk.
            ScheduleAfterChildren(() =>
            {
                if (selectionBefore != null && CurrentSelectionItem != null)
                    playSpinSample(visiblePanelCountBetweenItems(selectionBefore, CurrentSelectionItem));
            });

            return true;
        }

        private bool nextRandomBeatmap()
        {
            ICollection<BeatmapInfo> visibleBeatmaps = ExpandedGroup != null
                // In the case of grouping, users expect random to only operate on the expanded group.
                // This is going to incur some overhead as we don't have a group-beatmapset mapping currently.
                //
                // If this becomes an issue, we could either store a mapping, or run the random algorithm many times
                // using the `SetItems` method until we get a group HIT.
                ? grouping.GroupItems[ExpandedGroup].Select(i => i.Model).OfType<BeatmapInfo>().ToArray()
                : GetCarouselItems()!.Select(i => i.Model).OfType<BeatmapInfo>().ToArray();

            BeatmapInfo beatmap;

            switch (randomAlgorithm.Value)
            {
                case RandomSelectAlgorithm.RandomPermutation:
                {
                    ICollection<BeatmapInfo> notYetVisitedBeatmaps = visibleBeatmaps.Except(previouslyVisitedRandomBeatmaps).ToList();

                    if (!notYetVisitedBeatmaps.Any())
                    {
                        previouslyVisitedRandomBeatmaps.RemoveAll(b => visibleBeatmaps.Contains(b));
                        notYetVisitedBeatmaps = visibleBeatmaps;
                        if (CurrentSelection is BeatmapInfo beatmapInfo)
                            notYetVisitedBeatmaps = notYetVisitedBeatmaps.Except([beatmapInfo]).ToList();
                    }

                    if (notYetVisitedBeatmaps.Count == 0)
                        return false;

                    beatmap = notYetVisitedBeatmaps.ElementAt(RNG.Next(notYetVisitedBeatmaps.Count));
                    break;
                }

                case RandomSelectAlgorithm.Random:
                    beatmap = visibleBeatmaps.ElementAt(RNG.Next(visibleBeatmaps.Count));
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            RequestSelection(beatmap);
            return true;
        }

        private bool nextRandomSet()
        {
            ICollection<BeatmapSetInfo> visibleSets = ExpandedGroup != null
                // In the case of grouping, users expect random to only operate on the expanded group.
                // This is going to incur some overhead as we don't have a group-beatmapset mapping currently.
                //
                // If this becomes an issue, we could either store a mapping, or run the random algorithm many times
                // using the `SetItems` method until we get a group HIT.
                ? grouping.GroupItems[ExpandedGroup].Select(i => i.Model).OfType<BeatmapSetInfo>().ToArray()
                // This is the fastest way to retrieve sets for randomisation.
                : grouping.SetItems.Keys;

            BeatmapSetInfo set;

            switch (randomAlgorithm.Value)
            {
                case RandomSelectAlgorithm.RandomPermutation:
                {
                    ICollection<BeatmapSetInfo> notYetVisitedSets = visibleSets.Except(previouslyVisitedRandomBeatmaps.Select(b => b.BeatmapSet!)).ToList();

                    if (!notYetVisitedSets.Any())
                    {
                        previouslyVisitedRandomBeatmaps.RemoveAll(b => visibleSets.Contains(b.BeatmapSet!));
                        notYetVisitedSets = visibleSets;
                        if (CurrentSelection is BeatmapInfo beatmapInfo)
                            notYetVisitedSets = notYetVisitedSets.Except([beatmapInfo.BeatmapSet!]).ToList();
                    }

                    if (notYetVisitedSets.Count == 0)
                        return false;

                    set = notYetVisitedSets.ElementAt(RNG.Next(notYetVisitedSets.Count));
                    break;
                }

                case RandomSelectAlgorithm.Random:
                    set = visibleSets.ElementAt(RNG.Next(visibleSets.Count));
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            selectRecommendedDifficultyForBeatmapSet(set);
            return true;
        }

        public bool PreviousRandom()
        {
            var carouselItems = GetCarouselItems();

            if (carouselItems?.Any() != true)
                return false;

            while (randomHistory.Any())
            {
                var previousBeatmap = randomHistory[^1];
                randomHistory.RemoveAt(randomHistory.Count - 1);

                var previousBeatmapItem = carouselItems.FirstOrDefault(i => i.Model is BeatmapInfo b && b.Equals(previousBeatmap));

                if (previousBeatmapItem == null)
                    return false;

                if (CurrentSelection is BeatmapInfo beatmapInfo)
                {
                    if (randomAlgorithm.Value == RandomSelectAlgorithm.RandomPermutation)
                        previouslyVisitedRandomBeatmaps.Remove(beatmapInfo);

                    if (CurrentSelectionItem == null)
                        playSpinSample(0);
                    else
                        playSpinSample(visiblePanelCountBetweenItems(previousBeatmapItem, CurrentSelectionItem));
                }

                RequestSelection(previousBeatmap);
                return true;
            }

            return false;
        }

        private double visiblePanelCountBetweenItems(CarouselItem item1, CarouselItem item2) => Math.Ceiling(Math.Abs(item1.CarouselYPosition - item2.CarouselYPosition) / PanelBeatmapSet.HEIGHT);

        private void playSpinSample(double distance)
        {
            var chan = spinSample?.GetChannel();

            if (chan != null)
            {
                chan.Frequency.Value = 1f + Math.Clamp(distance / 200, 0, 1);
                chan.Play();
            }

            randomSelectSample?.Play();
        }

        #endregion
    }

    /// <summary>
    /// Defines a grouping header for a set of carousel items.
    /// </summary>
    public record GroupDefinition
    {
        /// <summary>
        /// The order of this group in the carousel, sorted using ascending order.
        /// </summary>
        public int Order { get; }

        /// <summary>
        /// The title of this group.
        /// </summary>
        public string Title { get; }

        private readonly string uncasedTitle;

        public GroupDefinition(int order, string title)
        {
            Order = order;
            Title = title;
            uncasedTitle = title.ToLowerInvariant();
        }

        public virtual bool Equals(GroupDefinition? other) => uncasedTitle == other?.uncasedTitle;

        public override int GetHashCode() => HashCode.Combine(uncasedTitle);
    }

    /// <summary>
    /// Defines a grouping header for a set of carousel items grouped by star difficulty.
    /// </summary>
    public record StarDifficultyGroupDefinition(int Order, string Title, StarDifficulty Difficulty) : GroupDefinition(Order, Title);
}
