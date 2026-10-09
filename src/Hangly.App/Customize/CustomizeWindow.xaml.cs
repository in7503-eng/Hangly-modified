//
//  CustomizeWindow.xaml.cs
//  Hangly
//
//  Where everything about Hangly is changed.
//

using Hangly.App.Import;
using Hangly.App.Services;
using Hangly.Core.Import;
using Hangly.Core.Lifecycle;
using Hangly.Core.Models;
using Hangly.Core.Settings;
using Hangly.Core.Studio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Globalization;

namespace Hangly.App.Customize;

/// <summary>The settings window.</summary>
public sealed partial class CustomizeWindow : Window
{
    private readonly SettingsStore store;
    private readonly ILaunchAtLogin launchAtLogin;
    private readonly Hangly.Core.Registry.RegistrySync registry;
    private readonly AppEnvironment environment;
    private readonly List<CharmTile> tiles = [];
    private readonly Dictionary<string, CharmTile> tilesById = new(StringComparer.Ordinal);
    private readonly List<ToggleButton> chips = [];
    private readonly PairingService _pairing = new();

    private CharmFilter filter = CharmFilter.All;
    private string query = string.Empty;
    private string? selectedCharmId;

    private bool isClosingForReal;
    private bool isLoading;

    private int selectedSlot;
    private CharmCatalogEntry? detailed;
    private readonly System.Collections.ObjectModel.ObservableCollection<SlotTile> slotTiles = [];
    private bool isRebuildingSlots;
    private bool isLoadingSlotSize;
    private string? lastSecret;

    public CustomizeWindow(
        SettingsStore store,
        ILaunchAtLogin launchAtLogin,
        Hangly.Core.Registry.RegistrySync registry,
        AppEnvironment environment)
    {
        this.store = store;
        this.launchAtLogin = launchAtLogin;
        this.registry = registry;
        this.environment = environment;

        isLoading = true;

        InitializeComponent();
        Title = "Hangly";

        // =========================================================================
        // STEP 1: WIRE ALL PAIRING BUTTONS & FIREBASE EVENT LISTENERS
        // =========================================================================
        GenerateCodeButton.Click += OnGenerateCodeClicked;
        CancelCodeButton.Click += OnCancelCodeClicked;
        ConnectRoomButton.Click += OnConnectRoomClicked;
        CancelConnectButton.Click += OnCancelConnectClicked;
        AcceptButton.Click += OnAcceptClicked;
        DeclineButton.Click += OnDeclineClicked;
        DisconnectButton.Click += OnDisconnectClicked;
        TabDisconnectButton.Click += OnDisconnectClicked;

        // Pairing Events
        _pairing.PairingRequested += OnPairingRequested;
        _pairing.PairingAccepted += OnPairingAccepted;
        _pairing.PairingDeclined += OnPairingDeclined;
        _pairing.Unpaired += OnUnpaired;
        _pairing.CharmChangedFromPartner += OnCharmChangedFromPartner;
        _pairing.CustomCharmReceivedFromPartner += OnCustomCharmReceivedFromPartner;
        _pairing.AutoConnected += OnAutoConnected;
        _pairing.PartnerPresenceChanged += OnPartnerPresenceChanged;

        // Populate local computer name in Connected Device tab
        MyDeviceNameText.Text = _pairing.MyDeviceName;

        // Auto-reconnect on startup if previously paired
        _pairing.TryAutoConnect();

        Branding.HanglyButtons.Brand(SupportButton);
        SupportButton.CornerRadius = Branding.HanglyButtons.Corner;
        SupportButton.Height = 44;
        SupportButton.Padding = new Thickness(20, 0, 20, 0);
        SupportButton.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        SupportButton.ActualThemeChanged += (_, _) => Branding.HanglyButtons.Brand(SupportButton);
        AppWindow.Closing += OnClosing;

        ResizeToDefault();

        ConfigureSliders();
        BuildCharmGrid();
        BuildRopeChoices();
        BuildAnchorChoices();
        BuildAbout();
        BuildStudio();
        BuildShortcuts();
        Load();

        store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            IsClosed = true;
            store.Changed -= OnStoreChanged;
        };
    }

    public bool IsClosed { get; private set; }
    public CharmDetail Detail { get; } = new();

    private void ResizeToDefault()
    {
        CentreForOpening();
        Interop.WindowIcon.Apply(this);
        FixTheSize();
    }

    public void CentreForOpening() => Interop.WindowPlacement.SizeAndCentre(this, 1120, 800);

    private void FixTheSize()
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            return;
        }

        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
    }

    public void AllowClose()
    {
        isClosingForReal = true;
        Close();
    }

    private OverlaySettings Overlay => store.Settings.Overlay;

    private void OnClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (isClosingForReal)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private void ConfigureSliders()
    {
        foreach ((Slider slider, double low, double high) in ((Slider, double, double)[])
            [(SizeSlider, 0.5, 2.0), (LengthSlider, 0.5, 2.0), (OpacitySlider, 0.2, 1.0),
            (VerticalSlider, 0, Hangly.Core.Models.PositionPicker.MaximumOffsetY)])
        {
            slider.Maximum = high;
            slider.Minimum = low;
            slider.StepFrequency = 0.05;
            slider.SmallChange = 0.05;
            slider.LargeChange = 0.1;
        }
    }

    private void BuildCharmGrid()
    {
        RebuildTiles();
        CharmThumbnails.Prune(environment.Charms.All);

        slotTiles.CollectionChanged += OnSlotsReordered;
        BuildCollections();
        BuildFilterChips();
        ShowResults();
    }

    private void RebuildTiles()
    {
        tiles.Clear();
        foreach (CharmCatalogEntry entry in environment.Charms.All)
        {
            if (!tilesById.TryGetValue(entry.Id, out CharmTile? tile))
            {
                tile = new CharmTile(entry);
                tilesById[entry.Id] = tile;
            }

            tiles.Add(tile);
        }

        var live = environment.Charms.All.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string stale in tilesById.Keys.Where(id => !live.Contains(id)).ToList())
        {
            tilesById.Remove(stale);
        }
    }

    private void BuildCollections()
    {
        var cards = new List<CollectionCard>();
        IReadOnlyList<CharmCollection> collections = environment.Charms.All
            .Any(entry => entry.CategoryId == CharmIndex.CustomCategoryId)
            ? [CharmIndex.CustomCollection, .. CharmCatalog.Collections]
            : CharmCatalog.Collections;

        foreach (CharmCollection collection in collections)
        {
            CharmTile[] members =
            [
                .. environment.Charms.All
                    .Where(entry => entry.CategoryId == collection.Id)
                    .Select(entry => tilesById.TryGetValue(entry.Id, out CharmTile? tile) ? tile : null)
                    .OfType<CharmTile>(),
            ];

            if (members.Length > 0)
            {
                cards.Add(new CollectionCard(collection, members));
            }
        }

        Collections.ItemsSource = cards;
    }

    private void OnCollectionTapped(object sender, TappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: CollectionCard card })
        {
            return;
        }

        filter = CharmFilter.Category(card.Id);
        HighlightChips();
        ShowResults();
    }

    private void BuildFilterChips()
    {
        AddChip("All", CharmFilter.All);
        AddChip("Favourites", CharmFilter.Favourites);
        AddChip("Recent", CharmFilter.Recent);
        foreach (CharmCategory category in environment.Charms.Categories)
        {
            AddChip(category.Name, CharmFilter.Category(category.Id));
        }

        HighlightChips();
    }

    private void AddChip(string label, CharmFilter which)
    {
        var chip = new ToggleButton { Content = label, Tag = which };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Show {label}");
        chip.Click += (sender, _) =>
        {
            filter = which;
            HighlightChips();
            ShowResults();
        };

        chips.Add(chip);
        FilterChips.Children.Add(chip);
    }

    private void HighlightChips()
    {
        foreach (ToggleButton chip in chips)
        {
            chip.IsChecked = Equals(chip.Tag, filter);
        }
    }

    private void ShowResults()
    {
        AppSettings settings = store.Settings;
        IReadOnlyList<CharmCatalogEntry> matches = CharmSearch.Apply(
            environment.Charms,
            filter,
            query,
            settings.Library.FavouriteCharmIds,
            settings.Library.RecentCharmIds);

        var groups = new List<CharmGroup>();
        if (matches.Count > 0)
        {
            if (filter is CharmFilter.Recently)
            {
                groups.Add(new CharmGroup(
                    "Recently hung",
                    [.. matches.Select(entry => tilesById[entry.Id])]));
            }
            else
            {
                foreach (IGrouping<string, CharmCatalogEntry> pack in matches.GroupBy(PackOf))
                {
                    groups.Add(new CharmGroup(pack.Key, [.. pack.Select(entry => tilesById[entry.Id])]));
                }
            }
        }

        Packs.ItemsSource = groups;
        CollectionsScroller.Visibility = filter is CharmFilter.Everything && string.IsNullOrWhiteSpace(query)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ShowEmptyState(matches.Count == 0);
        MarkChosen();
        MarkFavourites();

        ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void ShowEmptyState(bool isEmpty)
    {
        if (IsShowingRopes)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            ResultsScroller.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroller.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!isEmpty)
        {
            return;
        }

        if (query.Trim().Length > 0)
        {
            EmptyTitle.Text = "Nothing matches that";
            EmptyDetail.Text = $"No charm has \u201c{query.Trim()}\u201d in its name, its place or its materials.";
            return;
        }

        (EmptyTitle.Text, EmptyDetail.Text) = filter switch
        {
            CharmFilter.Favourite => (
                "No favourites yet",
                "Star a charm with the button in the corner of its tile and it will be waiting here."),
            CharmFilter.Recently => (
                "Nothing hung yet",
                "Charms you put on the cord show up here, most recent first."),
            _ => ("Nothing here", "This category has no charms in it."),
        };
    }

    private void MarkFavourites()
    {
        var favourites = store.Settings.Library.FavouriteCharmIds.ToHashSet(StringComparer.Ordinal);
        foreach (CharmTile tile in tiles)
        {
            tile.IsFavourite = favourites.Contains(tile.Id);
        }
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        query = sender.Text;
        ShowResults();
    }

    private void OnFavouriteClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string id })
        {
            ToggleFavourite(id);
        }
    }

    private void ToggleFavourite(string id)
    {
        store.Update(settings => settings with
        {
            Library = settings.Library.WithFavouriteToggled(id),
        });

        MarkFavourites();
        RefreshDetailState();

        if (filter is CharmFilter.Favourite)
        {
            ShowResults();
        }
    }

    private static string PackOf(CharmCatalogEntry entry)
    {
        if (Hangly.Core.Models.CharmId.IsCustom(entry.Id))
        {
            return Hangly.Core.Models.CharmIndex.CustomCategory.Name;
        }

        int slash = entry.FileName.LastIndexOf('/');
        return slash < 0 ? "Classics & Collection" : entry.FileName[..slash];
    }

    private void BuildRopeChoices() => BrowseMode.SelectedIndex = 0;

    private void RefreshRopes()
    {
        OverlaySettings overlay = Overlay;
        IReadOnlyList<RopeStyle> favourites = store.Settings.Library.FavouriteRopes;
        bool onlyFavourites = RopeFilter.SelectedItem == RopeFavouritesFilter;
        List<RopeChoiceItem> items = [.. RopeStyleTable.All
            .Where(style => !onlyFavourites || favourites.Contains(style))
            .Select(style => new RopeChoiceItem(style, RopeSwatches.PathFor(style), favourites.Contains(style), style == overlay.RopeStyle))];

        RopeList.ItemsSource = items;
        RopeList.SelectedItem = items.FirstOrDefault(item => item.Style == overlay.RopeStyle);
        RopeFavouritesEmpty.Visibility = onlyFavourites && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Detail.ShowRope(RopeStyleTable.DisplayNameOf(overlay.RopeStyle), items.FirstOrDefault(item => item.Style == overlay.RopeStyle)?.Swatch
            ?? (RopeSwatches.PathFor(overlay.RopeStyle) is string path ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)) : null));
    }

    private void OnRopeFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (IsShowingRopes)
        {
            RefreshRopes();
        }
    }

    private void OnRopeFavouriteClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is RopeStyle style)
        {
            store.Update(settings => settings with { Library = settings.Library.WithFavouriteRopeToggled(style) });
        }
    }

    private void OnBrowseModeChanged(object sender, SelectionChangedEventArgs args) => ApplyBrowseMode();

    private bool IsShowingRopes => BrowseMode.SelectedIndex == 1;

    private void ApplyBrowseMode()
    {
        bool ropes = IsShowingRopes;

        CharmTools.Visibility = ropes ? Visibility.Collapsed : Visibility.Visible;
        FilterChips.Visibility = ropes ? Visibility.Collapsed : Visibility.Visible;
        RopesScroller.Visibility = ropes ? Visibility.Visible : Visibility.Collapsed;

        if (ropes)
        {
            ResultsScroller.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            RefreshRopes();
            return;
        }

        ShowSelectedSlotInDetail();
        ShowResults();
    }

    private void OnRopeListClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not RopeChoiceItem item || item.Style == Overlay.RopeStyle)
        {
            return;
        }

        store.UpdateOverlay(overlay => overlay with { RopeStyle = item.Style });
    }

    private void BuildAnchorChoices() { }

    private void Load()
    {
        isLoading = true;
        try
        {
            OverlaySettings overlay = Overlay;

            CountChoice.SelectedIndex = overlay.CharmIds.Count - 1;
            if (IsShowingRopes)
            {
                RefreshRopes();
            }
            PositionSlider.Value = Math.Round(overlay.Position * 100);
            ShowPositionLabel();
            VerticalSlider.Value = Math.Clamp(overlay.OffsetY, VerticalSlider.Minimum, VerticalSlider.Maximum);
            ShowVerticalLabel();

            SizeSlider.Value = overlay.CharmSize;
            LengthSlider.Value = overlay.RopeLength;
            OpacitySlider.Value = overlay.Opacity;
            UpdateSliderLabels();

            ShowToggle.IsOn = overlay.IsEnabled;
            MotionChoice.SelectedIndex = (int)overlay.Motion;
            GlowChoice.SelectedIndex = (int)overlay.Glow;
            WindowModeChoice.SelectedIndex = (int)overlay.WindowMode;
            InteractionChoice.SelectedIndex = (int)overlay.Interaction;
            RopePhysicsChoice.SelectedIndex = (int)overlay.RopePhysics;
            StartupToggle.IsOn = overlay.StartupAnimation;
            Visibility offered = overlay.CharmIds.Any(Hangly.Core.Models.IntroTable.IsSpiderMan)
                ? Visibility.Visible
                : Visibility.Collapsed;
            SpiderManSection.Visibility = offered;
            FullscreenToggle.IsOn = overlay.HidesDuringFullscreenVideo;
            SoundToggle.IsOn = store.Settings.SoundEffectsEnabled;
            VolumeSlider.Value = Math.Round(store.Settings.SoundVolume * 100);
            VolumeSlider.IsEnabled = SoundToggle.IsOn;
            VolumeLabel.Text = $"Volume — {VolumeSlider.Value:0}%";
            LoginToggle.IsOn = store.Settings.LaunchAtLogin;
            NameBox.MaxLength = AppSettings.DisplayNameLimit;
            NameBox.Text = store.Settings.DisplayName;

            RebuildSlots();
            ShowResults();
        }
        finally
        {
            isLoading = false;
        }
    }

    private void OnNameKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter)
        {
            CommitName();
            args.Handled = true;
        }
    }

    private void OnNameCommitted(object sender, RoutedEventArgs args) => CommitName();

    private void CommitName()
    {
        if (isLoading)
        {
            return;
        }

        string chosen = NameBox.Text.Trim();
        if (chosen.Length == 0 || chosen == store.Settings.DisplayName)
        {
            NameBox.Text = store.Settings.DisplayName;
            return;
        }

        store.Update(settings => settings with { DisplayName = chosen });
        Diagnostics.Log("name changed");
    }

    private void UpdateSliderLabels()
    {
        SizeLabel.Text = $"Charm size — {SizeSlider.Value:P0}";
        LengthLabel.Text = $"Rope length — {LengthSlider.Value:P0}";
        OpacityLabel.Text = $"Opacity — {OpacitySlider.Value:P0}";
    }

    private void RebuildSlots()
    {
        CharmStackState stack = Overlay.Stack;
        IReadOnlyList<RopeCharm> places = stack.Places;
        selectedSlot = Math.Clamp(selectedSlot, 0, places.Count - 1);

        isRebuildingSlots = true;
        slotTiles.Clear();
        for (int index = 0; index < places.Count; index++)
        {
            slotTiles.Add(new SlotTile(
                index,
                environment.Charms.Find(places[index].Id).DisplayName,
                tilesById.GetValueOrDefault(places[index].Id)?.Image));
        }

        SlotList.ItemsSource ??= slotTiles;

        SlotList.SelectedIndex = selectedSlot;
        isRebuildingSlots = false;

        MoveUpButton.IsEnabled = selectedSlot > 0;
        MoveDownButton.IsEnabled = selectedSlot < places.Count - 1;

        ShowSlotSize();
        ShowSelectedSlotInDetail();
        CordSummary.Text = places.Count == 1
            ? "One charm hangs on the cord."
            : $"{places.Count} charms hang on the cord, from the top down.";
    }

    private void BuildShortcuts()
    {
        void Bind(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                action();
            };
            Nav.KeyboardAccelerators.Add(accelerator);
        }

        const Windows.System.VirtualKeyModifiers Ctrl = Windows.System.VirtualKeyModifiers.Control;
        Bind(Windows.System.VirtualKey.Number1, Ctrl, () => ShowSection("charms"));
        Bind(Windows.System.VirtualKey.Number2, Ctrl, () => ShowSection("create"));
        Bind(Windows.System.VirtualKey.Number3, Ctrl, () => ShowSection("appearance"));
        Bind(Windows.System.VirtualKey.Number4, Ctrl, () => ShowSection("about"));
        Bind(Windows.System.VirtualKey.O, Ctrl | Windows.System.VirtualKeyModifiers.Shift,
            () => store.UpdateOverlay(overlay => overlay with { IsEnabled = !overlay.IsEnabled }));
        Bind(Windows.System.VirtualKey.F, Ctrl, () =>
        {
            ShowSection("charms");
            SearchBox.Focus(FocusState.Keyboard);
        });
        Bind(Windows.System.VirtualKey.D, Ctrl, FavouriteSelection);
        Bind(Windows.System.VirtualKey.W, Ctrl, () => AppWindow.Hide());

        Nav.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnMoveKeys), handledEventsToo: true);
        ToolTipService.SetToolTip(MoveUpButton, $"Move up ({HanglyShortcuts.WindowsKeysOf(HanglyShortcut.MoveUp)})");
        ToolTipService.SetToolTip(MoveDownButton, $"Move down ({HanglyShortcuts.WindowsKeysOf(HanglyShortcut.MoveDown)})");
    }

    private void OnMoveKeys(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
            || !IsDown(Windows.System.VirtualKey.Menu)
            || IsDown(Windows.System.VirtualKey.Control)
            || IsDown(Windows.System.VirtualKey.Shift)
            || CharmsPage.Visibility != Visibility.Visible)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox)
        {
            return;
        }

        Button button = args.Key == Windows.System.VirtualKey.Up ? MoveUpButton : MoveDownButton;
        if (!button.IsEnabled)
        {
            return;
        }

        args.Handled = true;
        MoveSlot(args.Key == Windows.System.VirtualKey.Up ? -1 : 1);

        static bool IsDown(Windows.System.VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void FavouriteSelection()
    {
        if (CharmsPage.Visibility != Visibility.Visible)
        {
            return;
        }

        if (IsShowingRopes)
        {
            RopeStyle style = Overlay.RopeStyle;
            store.Update(settings => settings with { Library = settings.Library.WithFavouriteRopeToggled(style) });
            return;
        }

        if (detailed is not null)
        {
            ToggleFavourite(detailed.Id);
        }
    }

    private void OnMoveSlotUp(object sender, RoutedEventArgs args) => MoveSlot(-1);
    private void OnMoveSlotDown(object sender, RoutedEventArgs args) => MoveSlot(1);

    private void MoveSlot(int delta)
    {
        CharmStackState stack = Overlay.Stack;
        int destination = selectedSlot + delta;
        if (destination < 0 || destination >= stack.Count)
        {
            return;
        }

        int source = selectedSlot;
        store.UpdateOverlay(overlay => overlay.WithStack(overlay.Stack.Moved(source, destination)));

        selectedSlot = destination;
        RebuildSlots();
    }

    private void ShowSlotSize()
    {
        CharmStackState stack = Overlay.Stack;
        double size = stack.SizeAt(selectedSlot);

        isLoadingSlotSize = true;
        SlotSizeSlider.Value = size;
        isLoadingSlotSize = false;

        SlotSizeLabel.Text = $"Size of {environment.Charms.Find(stack.Ids[selectedSlot]).DisplayName} — {size:P0} of its own";
    }

    private void OnSlotSizeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (isLoading || isLoadingSlotSize)
        {
            return;
        }

        int slot = selectedSlot;
        double size = SlotSizeSlider.Value;
        store.UpdateOverlay(overlay => overlay.WithStack(overlay.Stack.WithSize(slot, size)));
        ShowSlotSize();
    }

    private void OnSlotItemClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is SlotTile tile)
        {
            selectedSlot = tile.Index;
            SlotList.SelectedIndex = selectedSlot;
            ShowSlotSize();
            ShowSelectedSlotInDetail();
        }
    }

    private void OnSlotsReordered(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (args.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            return;
        }

        if (isRebuildingSlots || isLoading)
        {
            return;
        }

        var order = slotTiles.Select(tile => tile.Index).ToList();
        if (order.SequenceEqual(Enumerable.Range(0, order.Count)))
        {
            return;
        }

        store.UpdateOverlay(overlay =>
        {
            IReadOnlyList<RopeCharm> before = overlay.Stack.Places;
            var reordered = order
                .Where(index => index >= 0 && index < before.Count)
                .Select(index => before[index])
                .ToList();

            return reordered.Count == before.Count
                ? overlay.WithStack(CharmStackState.FromPlaces(reordered))
                : overlay;
        });

        RebuildSlots();
    }

    private void MarkChosen()
    {
        var chosen = Overlay.CharmIds.ToHashSet(StringComparer.Ordinal);
        foreach (CharmTile tile in tiles)
        {
            tile.IsChosen = chosen.Contains(tile.Id);
        }
    }

    private void ShowSelectedSlotInDetail()
    {
        IReadOnlyList<string> ids = Overlay.CharmIds;
        if (selectedSlot >= 0 && selectedSlot < ids.Count)
        {
            ShowDetail(environment.Charms.Find(ids[selectedSlot]));
        }
    }

    private void OnSlotClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: int index })
        {
            selectedSlot = index;
            ShowSelectedSlotInDetail();
        }
    }

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string page = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "charms";
        ApplySection(page);
    }

    private void ApplySection(string page)
    {
        CharmsPage.Visibility = page == "charms" ? Visibility.Visible : Visibility.Collapsed;
        CreatePage.Visibility = page == "create" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "create")
        {
            StudioPane.Resume();
        }
        AppearancePage.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = page == "devices" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;

        DetailPanel.Visibility = page == "charms" ? Visibility.Visible : Visibility.Collapsed;

        if (page == "devices")
        {
            UpdateDevicesPageUI();
        }

        if (page == "about")
        {
            LoadInstallation();
        }
    }

    private Services.Updater updater => environment.Updates;

    private void OnShowWelcomeClicked(object sender, RoutedEventArgs args) =>
        environment.ShowWelcomeAgain();

    private async void OnCheckForUpdates(object sender, RoutedEventArgs args)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateMessage.Text = "Checking…";

        ShowUpdateResult(await updater.CheckAsync(Hangly.Core.Analytics.UpdateTrigger.Manual));
        CheckUpdateButton.IsEnabled = true;
    }

    private void ShowUpdateResult(Services.UpdateCheck result)
    {
        UpdateMessage.Text = result.Message;
        InstallUpdateButton.Visibility = result.HasUpdate ? Visibility.Visible : Visibility.Collapsed;

        UpdateNotes.Text = result.HasNotes ? Services.Updater.PlainNotes(result.Notes!) : string.Empty;
        UpdateNotesPanel.Visibility = UpdateNotes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowUpdates(Services.UpdateCheck found)
    {
        ShowSection("about");
        ShowUpdateResult(found);
    }

    public void ShowLibrary() => ShowSection("charms");
    public void ShowSectionNamed(string tag) => ShowSection(tag);

    public void ShowFromNotification(string? collectionId, string? charmId)
    {
        ShowSection("charms");
        query = string.Empty;
        SearchBox.Text = string.Empty;
        bool known = collectionId is not null && environment.Charms.Categories.Any(category => category.Id == collectionId);
        filter = known ? CharmFilter.Category(collectionId!) : CharmFilter.All;
        HighlightChips();
        ShowResults();
        if (charmId is not null && environment.Charms.Find(charmId) is { } entry && entry.Id == charmId)
        {
            ShowDetail(entry);
        }
    }

    private void ShowSection(string tag)
    {
        foreach (object item in Nav.MenuItems)
        {
            if (item is not NavigationViewItem entry || (entry.Tag as string) != tag)
            {
                continue;
            }

            if (ReferenceEquals(Nav.SelectedItem, entry))
            {
                ApplySection(tag);
            }
            else
            {
                Nav.SelectedItem = entry;
            }

            return;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs args)
    {
        InstallUpdateButton.IsEnabled = false;
        UpdateMessage.Text = "Downloading…";

        Microsoft.UI.Dispatching.DispatcherQueue queue = DispatcherQueue;
        UpdateMessage.Text = await updater.DownloadAndApplyAsync(status => queue.TryEnqueue(() => UpdateMessage.Text = status));
        InstallUpdateButton.IsEnabled = true;
        UpdateNotesPanel.Visibility = Visibility.Collapsed;
    }

    private Studio.StudioSession? studio;
    private int? studioSlot;

    private void BuildStudio()
    {
        studio = new Studio.StudioSession(
            () => Studio.OnnxSegmenter.IsInstalled ? new Studio.OnnxSegmenter() : null,
            SaveFromStudio);
        OverlaySettings overlay = store.Settings.Overlay;
        StudioPane.Attach(
            studio,
            overlay.RopeStyle,
            RopeMotionTable.Resolve(overlay.Motion, Services.SystemMotion.ReducesMotion),
            () => WinRT.Interop.WindowNative.GetWindowHandle(this));
        StudioPane.DoneRequested += () =>
        {
            studioSlot = null;
            AppWindow.Hide();
        };
        AppWindow.Changed += (_, change) =>
        {
            if (change.DidVisibilityChange && !AppWindow.IsVisible)
            {
                StudioPane.Release();
            }
        };
    }

    public void OpenInStudio(string path, int? slot)
    {
        studioSlot = slot;
        ShowSection("create");
        _ = StudioPane.OpenAsync(path);
    }

    private string SaveFromStudio(string markup, StudioDraft draft, string name, bool hang)
    {
        CustomCharmEntry entry = environment.CustomCharmsStore.Add(
            markup,
            name,
            draft.Metrics,
            draft.Palette);
        environment.CharmsChanged();

        RebuildTiles();
        BuildCollections();
        BuildFilterChips();
        ShowResults();

        string id = CharmId.ForCustom(entry.Id);
        int slot = studioSlot ?? selectedSlot;
        if (hang)
        {
            store.Update(settings => Hanging.Hang(settings, slot, id));
        }

        return entry.Name;
    }

    private void BuildAbout()
    {
        string? icon = AppIconImage.Path();
        if (icon is not null)
        {
            AppIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icon));
        }

        VersionLine.Text = $"Version {AppInfo.Version} (build {AppInfo.BuildNumber})";
        CopyrightLine.Text = AppInfo.Copyright;

        WebsiteLink.NavigateUri = new Uri(AppInfo.WebsiteUrl);
        GitHubLink.NavigateUri = new Uri(AppInfo.GitHubUrl);
        ReleaseNotesLink.NavigateUri = new Uri(AppInfo.ReleaseNotesUrl);
        InstagramLink.NavigateUri = new Uri(AppInfo.InstagramUrl);
        CreatorHandleLink.NavigateUri = new Uri(AppInfo.InstagramUrl);
        CreatorSiteLink.NavigateUri = new Uri(AppInfo.CreatorSiteUrl);
        CreatorName.Text = AppInfo.CreatorHandle;
        ShowMilestones();
    }

    private void ShowMilestones()
    {
        environment.BankSwings();

        MilestoneSettings milestones = store.Settings.Milestones;
        StatLaunches.Text = milestones.LaunchCount.ToString("N0", CultureInfo.CurrentCulture);
        StatCharms.Text = milestones.CharmsHung.ToString("N0", CultureInfo.CurrentCulture);
        StatSwings.Text = milestones.SwingsSurvived.ToString("N0", CultureInfo.CurrentCulture);
        StatSecrets.Text = milestones.SecretsFound.ToString("N0", CultureInfo.CurrentCulture);
    }

    private void OnSecretClicked(object sender, RoutedEventArgs args)
    {
        string secret = SecretVault.Reveal(Random.Shared, lastSecret);
        lastSecret = secret;
        SecretText.Text = secret;

        store.Update(settings => settings with
        {
            Milestones = settings.Milestones with
            {
                SecretsFound = settings.Milestones.SecretsFound + 1,
            },
        });

        ShowMilestones();
        environment.Overlay?.Nudge();
    }

    private void OnSuggestClicked(object sender, RoutedEventArgs args) =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(AppInfo.SuggestMailUrl));

    private void LoadInstallation()
    {
        Hangly.Core.Registry.InstallationRecord? record = registry.Store.Record;
        AnalyticsState.Text = !registry.IsConfigured
            ? "Not configured in this build"
            : record?.Uploaded is null
                ? registry.LastFailure is null ? "Waiting to register" : "Will retry when online"
                : $"Registered — last seen {record.LastSeen?.LocalDateTime:d}";
        AnalyticsEndpoint.Text = string.Join(", ", new[] { record?.City, record?.Region, record?.Country }.Where(part => !string.IsNullOrEmpty(part)))
            is { Length: > 0 } place ? place : "Not known yet";
        string id = record?.InstallationId.ToString("D") ?? string.Empty;
        AnalyticsIdentifier.Text = id.Length > 0 ? $"{id[..8]}-••••-••••-••••-••••••••{id[^4..]}" : "none yet";
        AnalyticsLastSent.Text = registry.LastAcceptedAt is DateTimeOffset at ? $"{at.LocalDateTime:HH:mm:ss}" : "nothing this session";
        AnalyticsUserName.Text = record is { Nickname.Length: > 0 } ? record.Nickname : "(not set)";
        AnalyticsFields.Text = "installationId, nickname, city, region, country, platform, osName, osVersion, appVersion, "
            + "architecture, firstSeen, lastSeen, activeDays, retentionDays, crash reports";
    }

    private void OnPrivacyDetails(object sender, RoutedEventArgs args)
    {
        ShowSection("about");
        AnalyticsSection.IsExpanded = true;
        AnalyticsSection.StartBringIntoView();
        LoadInstallation();
    }

    private void OnRefreshAnalytics(object sender, RoutedEventArgs args) => LoadInstallation();

    private async void OnSupportClicked(object sender, RoutedEventArgs args)
    {
        Hangly.Core.Analytics.HanglyAnalytics.Log(
            Hangly.Core.Analytics.AnalyticsEvent.SupportClicked(Hangly.Core.Analytics.SupportSurface.About));
        try
        {
            await SupportSheet.ShowAsync(Root);
        }
        catch (Exception exception)
        {
            Diagnostics.Failure("support sheet", exception);
        }
    }

    private void OnCharmClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not CharmTile tile)
        {
            return;
        }

        UpdateDeleteButton(tile.Id);
        ShowDetail(tile.Entry);

        store.Update(settings => Hanging.Hang(settings, selectedSlot, tile.Id));

        if (_pairing.IsConnected)
        {
            if (Hangly.Core.Models.CharmId.IsCustom(tile.Id))
            {
                Task.Run(async () =>
                {
                    string b64 = await TryGetCustomCharmBase64Async(tile.Id);
                    if (!string.IsNullOrEmpty(b64))
                    {
                        await _pairing.SendCustomCharmUpdateAsync(tile.Id, tile.DisplayName, b64);
                    }
                    else
                    {
                        await _pairing.SendCharmUpdateAsync(tile.Id);
                    }
                });
            }
            else
            {
                _ = _pairing.SendCharmUpdateAsync(tile.Id);
            }
        }
    }
    private void ShowDetail(CharmCatalogEntry? entry)
    {
        detailed = entry;
        Detail.Show(entry, entry is null ? null : tilesById.GetValueOrDefault(entry.Id)?.Image);
        RefreshDetailState();
    }

    private void RefreshDetailState()
    {
        if (detailed is null)
        {
            return;
        }

        AppSettings settings = store.Settings;
        Detail.IsOnRope = settings.Overlay.CharmIds.Contains(detailed.Id);
        Detail.IsFavourite = settings.Library.FavouriteCharmIds.Contains(detailed.Id);
    }

    private void OnCountChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isLoading || CountChoice.SelectedIndex < 0)
        {
            return;
        }

        int wanted = CountChoice.SelectedIndex + 1;
        store.UpdateOverlay(overlay => overlay.WithStack(overlay.Stack.WithCount(wanted)));
    }

    private void OnPositionChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        ShowPositionLabel();

        if (isLoading)
        {
            return;
        }

        double at = Math.Clamp(args.NewValue / 100, 0, 1);
        if (Math.Abs(Overlay.Position - at) < 0.0005)
        {
            return;
        }

        store.UpdateOverlay(overlay => overlay with { HorizontalPosition = at });
    }

    private void ShowPositionLabel() =>
        PositionLabel.Text = $"Horizontal position \u2014 {(int)Math.Round(PositionSlider.Value)}%";

    private void ShowVerticalLabel() =>
        VerticalLabel.Text = $"Vertical position \u2014 {(int)Math.Round(VerticalSlider.Value)} pt from the top";

    private void OnVerticalChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        ShowVerticalLabel();
        if (isLoading || Math.Abs(Overlay.OffsetY - args.NewValue) < 0.5)
        {
            return;
        }

        store.UpdateOverlay(overlay => overlay with { OffsetY = Math.Round(args.NewValue) });
    }

    private void OnResetPosition(object sender, RoutedEventArgs args)
    {
        OverlaySettings fresh = AppSettings.Defaults.Overlay;
        store.UpdateOverlay(overlay => overlay with { HorizontalPosition = fresh.HorizontalPosition, OffsetY = fresh.OffsetY });
    }

    private void OnSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        UpdateSliderLabels();
        if (!isLoading)
        {
            store.UpdateOverlay(overlay => overlay with { CharmSize = SizeSlider.Value });
        }
    }

    private void OnLengthChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        UpdateSliderLabels();
        if (!isLoading)
        {
            store.UpdateOverlay(overlay => overlay with { RopeLength = LengthSlider.Value });
        }
    }

    private void OnOpacityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        UpdateSliderLabels();
        if (!isLoading)
        {
            store.UpdateOverlay(overlay => overlay with { Opacity = OpacitySlider.Value });
        }
    }

    private void OnFullscreenToggled(object sender, RoutedEventArgs args)
    {
        if (!isLoading)
        {
            store.UpdateOverlay(overlay => overlay with { HidesDuringFullscreenVideo = FullscreenToggle.IsOn });
        }
    }

    private void OnSoundToggled(object sender, RoutedEventArgs args)
    {
        VolumeSlider.IsEnabled = SoundToggle.IsOn;
        if (!isLoading)
        {
            store.Update(settings => settings with { SoundEffectsEnabled = SoundToggle.IsOn });
        }
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        VolumeLabel.Text = $"Volume — {VolumeSlider.Value:0}%";
        if (!isLoading)
        {
            store.Update(settings => settings with { SoundVolume = VolumeSlider.Value / 100 });
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs args)
    {
        if (!isLoading)
        {
            bool on = StartupToggle.IsOn;
            store.UpdateOverlay(overlay => overlay with { StartupAnimation = on });
        }
    }

    private void OnRopePhysicsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!isLoading && RopePhysicsChoice.SelectedIndex is 0 or 1)
        {
            var physics = (Hangly.Core.Models.RopePhysics)RopePhysicsChoice.SelectedIndex;
            store.UpdateOverlay(overlay => overlay with { RopePhysics = physics });
        }
    }

    private void OnInteractionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!isLoading && InteractionChoice.SelectedIndex is 0 or 1)
        {
            var mode = (Hangly.Core.Models.InteractionMode)InteractionChoice.SelectedIndex;
            store.UpdateOverlay(overlay => overlay with { Interaction = mode });
        }
    }

    private void OnWindowModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!isLoading && WindowModeChoice.SelectedIndex is 0 or 1)
        {
            var mode = (Hangly.Core.Models.WindowMode)WindowModeChoice.SelectedIndex;
            store.UpdateOverlay(overlay => overlay with { WindowMode = mode });
        }
    }

    private void OnGlowChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!isLoading && GlowChoice.SelectedIndex is >= 0 and <= 2)
        {
            var glow = (Hangly.Core.Models.GlowLevel)GlowChoice.SelectedIndex;
            store.UpdateOverlay(overlay => overlay with { Glow = glow });
        }
    }

    private void OnMotionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!isLoading && MotionChoice.SelectedIndex is >= 0 and <= 2)
        {
            var motion = (Hangly.Core.Models.MotionPreference)MotionChoice.SelectedIndex;
            store.UpdateOverlay(overlay => overlay with { Motion = motion });
        }
    }

    private void OnShowToggled(object sender, RoutedEventArgs args)
    {
        if (!isLoading)
        {
            store.UpdateOverlay(overlay => overlay with { IsEnabled = ShowToggle.IsOn });
        }
    }

    private void OnLoginToggled(object sender, RoutedEventArgs args)
    {
        if (isLoading)
        {
            return;
        }

        launchAtLogin.SetEnabled(LoginToggle.IsOn);
        store.Update(settings => settings with { LaunchAtLogin = launchAtLogin.IsEnabled });
    }

    private void OnReset(object sender, RoutedEventArgs args) =>
        store.UpdateOverlay(_ => AppSettings.Defaults.Overlay);

    private void UpdateDeleteButton(string charmId)
    {
        selectedCharmId = charmId;
        DeleteButton.Visibility = Hangly.Core.Models.CharmId.IsCustom(charmId)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnImportClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            await ImportAsync();
        }
        catch (Exception exception)
        {
            Services.Diagnostics.Failure("import", exception);
            ImportMessage.Text = "That charm couldn't be imported.";
        }
    }

    private async Task ImportAsync()
    {
        string? path = Interop.FileDialog.OpenFile(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            "Import a charm",
            ("Pictures", "*.png;*.jpg;*.jpeg;*.webp"));

        Services.Diagnostics.Log($"import: chose {path ?? "nothing"}");
        if (path is null)
        {
            return;
        }

        ImportMessage.Text = "Applying glossy finish…";
        string glossyPath = await ApplyGlossAsync(path);

        ImportOutcome outcome = environment.ImportCharm(glossyPath);
        ImportMessage.Text = outcome.Message;

        if (!outcome.IsAccepted || outcome.Entry is null)
        {
            return;
        }

        RebuildTiles();
        RebuildChips();
        filter = CharmFilter.Category(Hangly.Core.Models.CharmIndex.CustomCategoryId);
        HighlightChips();
        UpdateDeleteButton(outcome.Entry.CharmId);

        chips.LastOrDefault()?.StartBringIntoView();

        store.Update(settings => Hanging.Hang(settings, selectedSlot, outcome.Entry.CharmId));

        ShowResults();

        if (_pairing.IsConnected)
        {
            try
            {
                byte[] imgBytes = await System.IO.File.ReadAllBytesAsync(glossyPath);
                string b64 = Convert.ToBase64String(imgBytes);
                _ = _pairing.SendCustomCharmUpdateAsync(outcome.Entry.CharmId, outcome.Entry.Name, b64);
            }
            catch { }
        }
    }

    private void Import()
    {
        string? path = Interop.FileDialog.OpenFile(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            "Import a charm",
            ("Pictures", "*.png;*.jpg;*.jpeg;*.webp"));

        Services.Diagnostics.Log($"import: chose {path ?? "nothing"}");
        if (path is null)
        {
            return;
        }

        ImportOutcome outcome = environment.ImportCharm(path);
        ImportMessage.Text = outcome.Message;

        if (!outcome.IsAccepted || outcome.Entry is null)
        {
            return;
        }

        RebuildTiles();
        RebuildChips();
        filter = CharmFilter.Category(Hangly.Core.Models.CharmIndex.CustomCategoryId);
        HighlightChips();
        UpdateDeleteButton(outcome.Entry.CharmId);

        chips.LastOrDefault()?.StartBringIntoView();

        store.Update(settings => Hanging.Hang(settings, selectedSlot, outcome.Entry.CharmId));

        ShowResults();
    }

    private void OnDeleteImportClicked(object sender, RoutedEventArgs args)
    {
        if (selectedCharmId is string id)
        {
            _ = ConfirmAndDeleteAsync(id);
        }
    }

    private void OnDeleteMenuClicked(object sender, RoutedEventArgs args)
    {
        if (sender is MenuFlyoutItem { Tag: string id })
        {
            _ = ConfirmAndDeleteAsync(id);
        }
    }

    private void OnFavouriteMenuClicked(object sender, RoutedEventArgs args)
    {
        if (sender is MenuFlyoutItem { Tag: string id })
        {
            ToggleFavourite(id);
        }
    }

    private async Task ConfirmAndDeleteAsync(string id)
    {
        if (!Hangly.Core.Models.CharmId.IsCustom(id))
        {
            return;
        }

        CustomCharmEntry? entry = environment.CustomCharms.Entries
            .FirstOrDefault(candidate => candidate.CharmId == id);

        if (entry is null || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(popup => popup.Child is ContentDialog))
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"Delete “{entry.Name}”?",
            Content = "The charm and its image are removed from Hangly. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(confirm, "DeleteCharmDialog");

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        environment.DeleteCharm(entry.Id);
        ImportMessage.Text = $"“{entry.Name}” was deleted.";
        if (selectedCharmId == id)
        {
            selectedCharmId = null;
            DeleteButton.Visibility = Visibility.Collapsed;
        }

        RebuildTiles();
        RebuildChips();
        if (filter is CharmFilter.OfCategory category
            && category.Id == Hangly.Core.Models.CharmIndex.CustomCategoryId
            && environment.Charms.Custom.Count == 0)
        {
            filter = CharmFilter.All;
        }

        HighlightChips();
        ShowResults();
    }

    private void RebuildChips()
    {
        chips.Clear();
        FilterChips.Children.Clear();
        BuildFilterChips();
    }

    private void OnStoreChanged(AppSettings settings) => Load();

    // =========================================================================
    // STEP 2: PAIRING LOGIC & EVENT HANDLERS
    // =========================================================================

    private void SetPairedUI(bool isPaired, string roomCode = "")
    {
        if (isPaired)
        {
            GenerateCodeButton.Visibility = Visibility.Collapsed;
            CancelCodeButton.Visibility = Visibility.Collapsed;
            RoomCodeText.Visibility = Visibility.Collapsed;
            RoomCodeInput.Visibility = Visibility.Collapsed;
            ConnectRoomButton.Visibility = Visibility.Collapsed;
            CancelConnectButton.Visibility = Visibility.Collapsed;
            RequestAlertBox.Visibility = Visibility.Collapsed;

            DisconnectButton.Visibility = Visibility.Visible;
            PartnerPresencePanel.Visibility = Visibility.Visible;
            PairingStatusText.Text = $"Status: Paired with {_pairing.PartnerDeviceName ?? "partner"} (Room: {roomCode})";

            // 🟢 Set directly to Green upon connection:
            PartnerStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65));
            PartnerPresenceText.Text = "Partner is Online";

            UpdateDevicesPageUI();
        }
        else
        {
            GenerateCodeButton.Visibility = Visibility.Visible;
            GenerateCodeButton.IsEnabled = true;
            CancelCodeButton.Visibility = Visibility.Visible;
            CancelCodeButton.IsEnabled = false;
            RoomCodeText.Visibility = Visibility.Visible;
            RoomCodeText.Text = "Code: ----";
            RoomCodeInput.Visibility = Visibility.Visible;
            RoomCodeInput.Text = "";
            ConnectRoomButton.Visibility = Visibility.Visible;
            ConnectRoomButton.IsEnabled = true;
            CancelConnectButton.Visibility = Visibility.Visible;
            CancelConnectButton.IsEnabled = false;
            RequestAlertBox.Visibility = Visibility.Collapsed;

            DisconnectButton.Visibility = Visibility.Collapsed;
            PartnerPresencePanel.Visibility = Visibility.Visible;

            // ⚪ Set directly to Gray when disconnected:
            PartnerStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 136, 134));
            PartnerPresenceText.Text = "Not connected";
            PairingStatusText.Text = "Status: Not connected";

            UpdateDevicesPageUI();
        }
    }

    private void UpdateDevicesPageUI()
    {
        MyDeviceNameText.Text = _pairing.MyDeviceName;
        if (_pairing.IsConnected)
        {
            DevicesPairedInfo.Visibility = Visibility.Visible;
            DevicesNotPairedInfo.Visibility = Visibility.Collapsed;
            PartnerDeviceNameText.Text = _pairing.PartnerDeviceName ?? "Partner Device";
            DevicesRoomCodeText.Text = _pairing.CurrentRoomCode ?? "----";
            DevicesStatusDot.Fill = PartnerStatusDot.Fill;
            DevicesStatusText.Text = PartnerPresenceText.Text;
        }
        else
        {
            DevicesPairedInfo.Visibility = Visibility.Collapsed;
            DevicesNotPairedInfo.Visibility = Visibility.Visible;
        }
    }

    private void OnAutoConnected(string roomCode)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetPairedUI(true, roomCode);
        });
    }

    private async void OnDisconnectClicked(object sender, RoutedEventArgs e)
    {
        await _pairing.DisconnectAndUnpairAsync();
        SetPairedUI(false);
    }

    private void OnUnpaired()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetPairedUI(false);
        });
    }

    private void OnPartnerPresenceChanged(bool isOnline)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdatePartnerPresenceUI(isOnline);
        });
    }

    private void UpdatePartnerPresenceUI(bool isOnline)
    {
        PartnerPresencePanel.Visibility = Visibility.Visible;
        if (isOnline)
        {
            // 🟢 Green dot
            PartnerStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65));
            PartnerPresenceText.Text = "Partner is Online";
        }
        else
        {
            // ⚪ Gray dot
            PartnerStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 136, 134));
            PartnerPresenceText.Text = _pairing.IsConnected ? "Partner is Offline" : "Not connected";
        }

        DevicesStatusDot.Fill = PartnerStatusDot.Fill;
        DevicesStatusText.Text = PartnerPresenceText.Text;
    }

    private void OnCharmChangedFromPartner(string charmId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            store.Update(settings => Hanging.Hang(settings, selectedSlot, charmId));
        });
    }

    private async void OnGenerateCodeClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            PairingStatusText.Text = "Status: Connecting to Firebase...";
            GenerateCodeButton.IsEnabled = false;

            string currentCharm = Overlay.CharmIds.Count > 0 ? Overlay.CharmIds[0] : "default";
            string code = await _pairing.GenerateRoomCodeAsync(currentCharm);

            RoomCodeText.Text = $"Code: {code}";
            CancelCodeButton.IsEnabled = true;
            PairingStatusText.Text = "Status: Waiting for partner to connect...";

            // 🟡 Orange dot while waiting
            PartnerStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 247, 99, 12));
            PartnerPresenceText.Text = "Waiting for partner...";
        }
        catch (Exception ex)
        {
            GenerateCodeButton.IsEnabled = true;
            PairingStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async void OnCancelCodeClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await _pairing.CancelRoomAsync();
            RoomCodeText.Text = "Code: ----";
            GenerateCodeButton.IsEnabled = true;
            CancelCodeButton.IsEnabled = false;
            RequestAlertBox.Visibility = Visibility.Collapsed;
            PairingStatusText.Text = "Status: Room cancelled. Not connected";
            UpdatePartnerPresenceUI(false);
        }
        catch (Exception ex)
        {
            PairingStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async void OnConnectRoomClicked(object sender, RoutedEventArgs e)
    {
        string code = RoomCodeInput.Text.Trim();
        if (code.Length == 4)
        {
            try
            {
                PairingStatusText.Text = "Status: Sending connection request...";
                ConnectRoomButton.IsEnabled = false;
                CancelConnectButton.IsEnabled = true;

                // 🟡 Orange dot while connecting
                PartnerStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 247, 99, 12));
                PartnerPresenceText.Text = "Connecting...";

                bool success = await _pairing.RequestJoinRoomAsync(code);
                if (!success)
                {
                    PairingStatusText.Text = "Status: Room not found. Check code.";
                    ConnectRoomButton.IsEnabled = true;
                    CancelConnectButton.IsEnabled = false;
                    UpdatePartnerPresenceUI(false);
                }
            }
            catch (Exception ex)
            {
                PairingStatusText.Text = $"Error: {ex.Message}";
                ConnectRoomButton.IsEnabled = true;
                CancelConnectButton.IsEnabled = false;
                UpdatePartnerPresenceUI(false);
            }
        }
        else
        {
            PairingStatusText.Text = "Status: Please enter a 4-digit code";
        }
    }

    private async void OnCancelConnectClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            PairingStatusText.Text = "Status: Cancelling request...";
            await _pairing.CancelRequestAsync();
            ConnectRoomButton.IsEnabled = true;
            CancelConnectButton.IsEnabled = false;
            PairingStatusText.Text = "Status: Request cancelled. Not connected";
            UpdatePartnerPresenceUI(false);
        }
        catch (Exception ex)
        {
            PairingStatusText.Text = $"Error: {ex.Message}";
        }
    }

    // Host receives pair request from Guest -> shows inline RequestAlertBox with Guest PC Name
    private void OnPairingRequested(string guestId, string guestDeviceName)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RequestAlertText.Text = string.IsNullOrEmpty(guestDeviceName)
                ? "Device wants to pair!"
                : $"{guestDeviceName} wants to pair!";
            RequestAlertBox.Visibility = Visibility.Visible;
            PairingStatusText.Text = $"Status: Incoming pair request from {guestDeviceName}!";
        });
    }

    // Host clicks Accept button in RequestAlertBox
    private async void OnAcceptClicked(object sender, RoutedEventArgs e)
    {
        RequestAlertBox.Visibility = Visibility.Collapsed;
        await _pairing.RespondToRequestAsync(true);
        SetPairedUI(true, _pairing.CurrentRoomCode ?? "");
    }

    // Host clicks Decline button in RequestAlertBox
    private async void OnDeclineClicked(object sender, RoutedEventArgs e)
    {
        RequestAlertBox.Visibility = Visibility.Collapsed;
        PairingStatusText.Text = "Status: Request declined";
        await _pairing.RespondToRequestAsync(false);
    }

    // Guest gets accepted
    private void OnPairingAccepted()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetPairedUI(true, _pairing.CurrentRoomCode ?? "");
        });
    }

    // Guest gets declined
    private void OnPairingDeclined()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ConnectRoomButton.IsEnabled = true;
            CancelConnectButton.IsEnabled = false;
            PairingStatusText.Text = "Status: Connection was declined by host";
            UpdatePartnerPresenceUI(false);
        });
    }
    // =========================================================================
    // PARTNER CUSTOM CHARM SYNC & GLOSSY FINISH
    // =========================================================================

    private void OnCustomCharmReceivedFromPartner(string name, string base64Data)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64Data);
                string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"partner_{Guid.NewGuid():N}.png");
                await System.IO.File.WriteAllBytesAsync(tempFile, bytes);

                ImportOutcome outcome = environment.ImportCharm(tempFile);
                if (outcome.IsAccepted && outcome.Entry is not null)
                {
                    RebuildTiles();
                    RebuildChips();
                    store.Update(settings => Hanging.Hang(settings, selectedSlot, outcome.Entry.CharmId));
                    ShowResults();
                }
            }
            catch (Exception ex)
            {
                Services.Diagnostics.Log($"Failed to import partner custom charm: {ex.Message}");
            }
        });
    }

    private static async Task<string> TryGetCustomCharmBase64Async(string charmId)
    {
        try
        {
            string localFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hangly");
            if (System.IO.Directory.Exists(localFolder))
            {
                string cleanId = charmId.Replace("custom:", "");
                string[] matchFiles = System.IO.Directory.GetFiles(localFolder, "*.*", System.IO.SearchOption.AllDirectories);
                foreach (var f in matchFiles)
                {
                    if (System.IO.Path.GetFileNameWithoutExtension(f).Contains(cleanId, StringComparison.OrdinalIgnoreCase) &&
                        (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)))
                    {
                        byte[] bytes = await System.IO.File.ReadAllBytesAsync(f);
                        return Convert.ToBase64String(bytes);
                    }
                }
            }
        }
        catch { }
        return "";
    }

    private static async Task<string> ApplyGlossAsync(string inputPath)
    {
        try
        {
            var inputFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(inputPath);
            using var inputStream = await inputFile.OpenAsync(Windows.Storage.FileAccessMode.Read);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inputStream);
            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);

            byte[] pixels = pixelData.DetachPixelData();
            int width = (int)decoder.OrientedPixelWidth;
            int height = (int)decoder.OrientedPixelHeight;

            // Specular curved glass highlight
            float cx = width * 0.42f;
            float cy = height * 0.28f;
            float rx = width * 0.40f;
            float ry = height * 0.22f;

            for (int y = 0; y < height; y++)
            {
                float dy = (y - cy) / ry;
                float dy2 = dy * dy;

                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    byte a = pixels[idx + 3];
                    if (a < 15) continue; // Keep transparent cutout background clear

                    float dx = (x - cx) / rx;
                    float dist2 = dx * dx + dy2;

                    float shine = 0f;
                    if (dist2 < 1.0f)
                    {
                        float s = 1.0f - dist2;
                        shine = s * s * 0.45f;
                    }

                    if (y < height * 0.15f)
                    {
                        float rim = (1.0f - (float)y / (height * 0.15f)) * 0.25f;
                        shine = Math.Max(shine, rim);
                    }

                    if (shine > 0f)
                    {
                        float blend = shine * (a / 255.0f);
                        pixels[idx] = (byte)(pixels[idx] + (255 - pixels[idx]) * blend);
                        pixels[idx + 1] = (byte)(pixels[idx + 1] + (255 - pixels[idx + 1]) * blend);
                        pixels[idx + 2] = (byte)(pixels[idx + 2] + (255 - pixels[idx + 2]) * blend);
                    }
                }
            }

            var tempFolder = Windows.Storage.ApplicationData.Current.TemporaryFolder;
            var outFile = await tempFolder.CreateFileAsync($"glossy_{Guid.NewGuid():N}.png", Windows.Storage.CreationCollisionOption.GenerateUniqueName);
            using (var outStream = await outFile.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
            {
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, outStream);
                encoder.SetPixelData(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                    (uint)width,
                    (uint)height,
                    decoder.DpiX,
                    decoder.DpiY,
                    pixels);
                await encoder.FlushAsync();
            }

            return outFile.Path;
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Log($"Gloss effect fallback: {ex.Message}");
            return inputPath;
        }
    }
}