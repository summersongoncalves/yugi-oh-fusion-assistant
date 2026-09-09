using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YgoFm.Core;
using YgoFm.Vision;
// See BitmapInterop.cs for why System.Drawing is aliased rather than imported wholesale.
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace YgoFm.App;

/// <summary>
/// The whole program, for now: pick the emulator window, mark where the hand cards and the
/// card-name panel are, then watch continuously — showing what the recogniser thinks is in each
/// slot, teaching the personal template library from the OCR'd name of whichever card is
/// currently selected, and listing whatever monster fusions the recognised hand allows. The
/// "Avançado" menu (<see cref="ListLearnedCards_Click"/>, <see cref="ClearLearning_Click"/>)
/// exists purely to inspect and reset that teaching process while testing it.
/// </summary>
public partial class MainWindow : Window
{
    // A target cadence, not a hard budget: the _busy guard (see Timer_Tick) means a tick that
    // is still running when this interval elapses again just gets skipped, never overlapped, so
    // setting this low never causes concurrent capture/recognition — it only controls how long
    // the app waits after a FAST tick before starting the next one.
    //
    // That distinction is the whole reason this is 300ms and not something closer to the
    // recognizer's own cost. Official-art matching (CardArtLibrary.Match's multi-scale search)
    // still measures 1.4-1.8s for 5 slots regardless of this value — _busy is what actually
    // paces that case, not PollInterval. But once a card is taught (HandReader.ReadSlot's
    // taught-library check, which skips OCR and the slow search entirely for a confident
    // match), a tick can finish in a small fraction of that. The old 2000ms made every one of
    // those fast ticks wait out a fixed 2-second gap regardless — pure idle time once the
    // library already knows the hand. 300ms keeps a slow tick exactly as slow as its own work
    // makes it, while letting a fast one repeat close to back-to-back.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);

    private readonly ObservableCollection<SlotReadingView> _rows = [];

    // UIElement, not a view-model type: see FusionRowBuilder for why each row is a whole
    // ready-made WrapPanel rather than data an ItemsControl DataTemplate renders.
    private readonly ObservableCollection<UIElement> _fusionRows = [];

    /// <summary>The three states the media-player-style controls (see MainWindow.xaml) switch
    /// between. Idle shows only Iniciar; Running and Paused both show Pausar/Continuar and
    /// Parar, differing only in whether the polling timer is actually ticking.</summary>
    private enum ObservingState { Idle, Running, Paused }

    private ObservingState _state = ObservingState.Idle;

    private CardDatabase? _cards;
    private CardArtLibrary? _art;
    private TaughtCardLibrary? _taught;
    private HandReader? _reader;
    private CardThumbnailCache? _thumbnails;

    private IntPtr _targetWindow;
    private string _targetWindowTitle = "";
    private NormRect? _handRegion;
    private NormRect? _nameRegion;
    private DispatcherTimer? _timer;
    private bool _busy;

    // Debounces teaching against transition frames (turn changes, animations, menus) — the
    // vision and OCR readings during those are transient and sometimes coincide well enough to
    // look confident for a single tick, which was observed to teach the wrong card. A genuine
    // player-held selection stays on screen for seconds; a transition does not, so requiring the
    // same (slot, card) pairing to repeat across consecutive ticks before acting on it filters
    // out the transient case without needing to recognise what a transition actually looks like.
    //
    // This is a nullable tuple rather than three separate fields so "no candidate yet" has one
    // clean representation (null) instead of needing a sentinel like SlotIndex = -1. Once a tick
    // sees the same pairing again, `pending with { Streak = ... }` produces a new tuple value
    // with just that field changed — value-type tuples (and records) have no in-place mutation,
    // so "updating" one always means replacing the field that holds it, which is exactly what
    // the assignment back into _pendingTeach does.
    private (int SlotIndex, int CardId, int Streak)? _pendingTeach;

    /// <summary>How many consecutive ticks must agree before a teach is actually written. Picked
    /// to be small enough not to feel unresponsive at the ~2s tick cadence, not yet validated
    /// against how long a real transition lasts — turn this up if wrong teaches still slip through.</summary>
    private const int TeachStreakRequired = 2;

    public MainWindow()
    {
        InitializeComponent();
        ReadingsList.ItemsSource = _rows;
        FusionsList.ItemsSource = _fusionRows;
        UpdateControlButtons();
        Closed += (_, _) => StopObserving();
    }

    // ------------------------------------------------------------ Start / Pause / Stop

    private void Start_Click(object sender, RoutedEventArgs e) => StartObserving();

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        // Pause is deliberately lighter than Stop: it only stops the DispatcherTimer, leaving
        // _targetWindow/_handRegion/_nameRegion/_pendingTeach exactly as they are. Resuming just
        // restarts that same timer — no re-picking the emulator window or the regions, which is
        // the whole point of having this separate from Stop (see StopObserving, which does clear
        // everything and always sends the user back through both pickers on the next Iniciar).
        if (_state == ObservingState.Running)
        {
            _timer?.Stop();
            _state = ObservingState.Paused;
            Status($"Pausado. '{_targetWindowTitle}' — clique em Continuar para retomar sem escolher tudo de novo.");
        }
        else if (_state == ObservingState.Paused)
        {
            _timer?.Start();
            _state = ObservingState.Running;
            Status($"Observando '{_targetWindowTitle}'.");
        }

        UpdateControlButtons();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopObserving();

    /// <summary>
    /// Shows/hides the three top buttons and flips the Pausar/Continuar icon and label to match
    /// <see cref="_state"/>. Called after every state change instead of scattering
    /// Visibility/Content assignments across each handler, so there is exactly one place that
    /// says what each state looks like.
    /// </summary>
    private void UpdateControlButtons()
    {
        StartMenuButton.Visibility = _state == ObservingState.Idle ? Visibility.Visible : Visibility.Collapsed;
        PauseMenuButton.Visibility = _state == ObservingState.Idle ? Visibility.Collapsed : Visibility.Visible;
        StopMenuButton.Visibility = _state == ObservingState.Idle ? Visibility.Collapsed : Visibility.Visible;

        PauseIcon.Text = _state == ObservingState.Paused ? "▶" : "⏸";
        PauseLabel.Text = _state == ObservingState.Paused ? "Continuar" : "Pausar";
    }

    private void StartObserving()
    {
        if (!EnsureDataLoaded()) return;

        var picker = new SelectEmulatorWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.Chosen is not { } target) return;

        DrawingBitmap snapshot;
        try
        {
            // We read pixels off the composited desktop, so the target has to be on top,
            // and it needs a moment to actually finish drawing after being raised.
            ScreenCapture.BringToFront(target.Handle);
            System.Threading.Thread.Sleep(250);
            snapshot = ScreenCapture.CaptureWindow(target.Handle);
        }
        catch (Exception ex)
        {
            Status($"Não foi possível capturar '{target.Title}': {ex.Message}");
            return;
        }

        if (ScreenCapture.LooksBlank(snapshot))
        {
            snapshot.Dispose();
            Status($"A captura de '{target.Title}' saiu de uma cor só — o backend gráfico dessa janela bloqueia " +
                   "esse tipo de captura. Tente outro modo de renderização no emulador.");
            return;
        }

        using (snapshot)
        {
            var regionPicker = new SelectRegionWindow(snapshot, "Selecione as regiões",
                [
                    new SelectRegionWindow.Stage("Marque a região das 5 cartas da mão " +
                        "(inclua a faixa de ATQ/DEF — é nela que aparece a setinha da carta selecionada)"),
                    new SelectRegionWindow.Stage("Agora marque o painel do nome da carta " +
                        "(pode marcar com folga, largo o bastante para o nome mais comprido do jogo)"),
                ])
            { Owner = this };
            if (regionPicker.ShowDialog() != true) return;

            var handRegion = regionPicker.Selections[0];
            var nameRegion = regionPicker.Selections[1];

            _targetWindow = target.Handle;
            _targetWindowTitle = target.Title;
            _handRegion = handRegion;
            _nameRegion = nameRegion;

            // The verification habit from CLAUDE.md: save what was actually selected, so a
            // wrong region can be diagnosed after the fact instead of only guessed at.
            var folder = ProjectPaths.NewCaptureFolder(DateTime.Now);
            snapshot.Save(Path.Combine(folder, "00-snapshot.png"), System.Drawing.Imaging.ImageFormat.Png);
            using (var marked = PreviewAnnotator.Annotate(snapshot,
                       [new SlotReading
                       {
                           Slot = 0, SlotBounds = handRegion.ToPixels(new DrawingRectangle(DrawingPoint.Empty, snapshot.Size)),
                           ArtBounds = handRegion.ToPixels(new DrawingRectangle(DrawingPoint.Empty, snapshot.Size)),
                           Verdict = SlotVerdict.Confident,
                       }]))
                marked.Save(Path.Combine(folder, "01-selected-region.png"), System.Drawing.Imaging.ImageFormat.Png);

            using var namePanelCrop = FrameCropper.Crop(snapshot,
                nameRegion.ToPixels(new DrawingRectangle(DrawingPoint.Empty, snapshot.Size)));
            namePanelCrop.Save(Path.Combine(folder, "02-name-panel.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        Status($"Observando '{target.Title}'.");
        _pendingTeach = null;
        UpdateLearningStatus();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        _state = ObservingState.Running;
        UpdateControlButtons();
    }

    // ------------------------------------------------------------ Avançado menu
    //
    // Both handlers call EnsureDataLoaded() first rather than assuming Start has already run —
    // a user could open this menu before ever clicking Start, and _cards/_taught would still be
    // null at that point. EnsureDataLoaded() is the same lazy-init used at the top of
    // StartObserving, so calling it twice (once from here, once later from Start) is harmless:
    // its very first line returns immediately if everything is already loaded.

    private void ListLearnedCards_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDataLoaded()) return;

        // ShowDialog() (not Show()) blocks this window's input until the list window closes.
        // There is no ongoing interaction between the two windows once it is open — it just
        // shows a snapshot of what has been learned — so a modal dialog is simplest here.
        new LearnedCardsWindow(_cards!, _taught!) { Owner = this }.ShowDialog();
    }

    private void ClearLearning_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDataLoaded()) return;

        var count = _taught!.Count;
        if (count == 0)
        {
            MessageBox.Show(this, "Nenhuma carta foi aprendida ainda.", "Aprendizado vazio",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Destructive and irreversible (see TaughtCardLibrary.Clear — it deletes the PNGs, not
        // just the in-memory index), so this asks first rather than acting on a menu click alone.
        var confirmed = MessageBox.Show(this,
            $"Isso apaga as {count} carta(s) aprendidas nesta máquina (as imagens em data/templates/). " +
            "Não afeta a base de cartas nem a arte oficial. Continuar?",
            "Limpar aprendizado", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes) return;

        _taught.Clear();
        _pendingTeach = null; // otherwise a streak already in progress could re-teach on the very next tick
        UpdateLearningStatus();
        Status("Aprendizado limpo.");
    }

    /// <summary>
    /// Built as a plain Window in code, the same way SelectRegionWindow's guide-image popup is —
    /// its whole content is a handful of TextBlocks and a copyable email/Pix key, not enough to
    /// justify a dedicated .xaml file.
    /// </summary>
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = "🇧🇷 Made in Brazil",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Feito por Summerson Gonçalves.",
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Contato / chave Pix:",
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.Gray,
            FontSize = 11,
        });

        // A read-only TextBox (not a TextBlock) so the email/Pix key can be selected and
        // copied with Ctrl+C — a TextBlock's text is not selectable by default in WPF.
        panel.Children.Add(new TextBox
        {
            Text = "summersongoncalves@gmail.com",
            IsReadOnly = true,
            TextAlignment = TextAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 16),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Se esse app te ajudou a montar fusões melhores, um cafezinho via Pix " +
                   "(mesma chave acima) é sempre muito bem-vindo! ☕",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        var okButton = new Button { Content = "Fechar", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 16, 0, 0), IsDefault = true, IsCancel = true };
        panel.Children.Add(okButton);
        okButton.HorizontalAlignment = HorizontalAlignment.Center;

        var about = new Window
        {
            Title = "Sobre",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        okButton.Click += (_, _) => about.Close();
        about.ShowDialog();
    }

    private void StopObserving()
    {
        if (_timer is null) return;

        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _timer = null;

        _state = ObservingState.Idle;
        UpdateControlButtons();
        Status("Parado. Clique em Iniciar para escolher a janela e as regiões novamente.");
    }

    private bool EnsureDataLoaded()
    {
        if (_cards is not null && _art is not null && _taught is not null) return true;

        try
        {
            _cards = CardDatabase.Load(ProjectPaths.CardsFile);
            _art = CardArtLibrary.Load(ProjectPaths.CardArtFile, _cards.Count);
            _taught = TaughtCardLibrary.Load(ProjectPaths.Templates);
            _reader = new HandReader(_cards, _art, _taught);
            _thumbnails = new CardThumbnailCache(_art);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Não consegui carregar a base de cartas:\n\n{ex.Message}\n\n" +
                $"Esperado em:\n{ProjectPaths.CardsFile}\n{ProjectPaths.CardArtFile}",
                "Base de cartas ausente", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ------------------------------------------------------------ observing loop

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_busy || _reader is null || _handRegion is not { } handRegion || _nameRegion is not { } nameRegion) return;
        _busy = true;

        try
        {
            var result = await CaptureAndReadAsync(handRegion, nameRegion);

            if (result.Error is not null)
            {
                Status(result.Error);
                StopObserving();
                return;
            }

            if (result.Inactive)
            {
                // Deliberately leaves PreviewImage and _rows untouched — the last real frame
                // stays on screen rather than being replaced by anything synthetic.
                Status($"'{_targetWindowTitle}' não está em primeiro plano — pausado até você voltar a ela. " +
                       "Mostrando o último quadro observado.");
                return;
            }

            Status($"Observando '{_targetWindowTitle}'.");

            if (result.Annotated is not null)
            {
                PreviewImage.Source = result.Annotated.ToBitmapSource();
                result.Annotated.Dispose();
            }

            _rows.Clear();
            if (result.Rows is not null)
                foreach (var row in result.Rows) _rows.Add(row);

            // Built here, not in CaptureAndReadCoreAsync, because this is the UI thread — see
            // the field comment on ReadResult.Fusions for why that boundary matters.
            _fusionRows.Clear();
            if (result.Fusions is not null)
                foreach (var chain in result.Fusions) _fusionRows.Add(FusionRowBuilder.Build(chain, _thumbnails!));

            // Set directly rather than through a data binding + IValueConverter (the more
            // "proper WPF" way to turn a count into a Visibility): there is nothing else in this
            // view model that needs one, so a one-line imperative check here is less machinery
            // for the same one relationship. Collapsed (not Hidden) removes it from layout too,
            // so it does not reserve blank space over the ListView while rows are showing.
            NoFusionsText.Visibility = _fusionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateLearningStatus(result.JustLearned, result.TeachDiagnostic);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// <paramref name="diagnostic"/> is shown every tick regardless of whether anything was
    /// learned — this is the visible trace of TryTeachAsync's internal state (which stage it
    /// reached, and why it stopped there) that replaced silently guessing from the outside
    /// when the whole pipeline checked out fine in isolation but still would not teach live.
    /// </summary>
    private void UpdateLearningStatus(string? justLearned = null, string? diagnostic = null)
    {
        var count = _taught?.Count ?? 0;
        var total = _cards?.Count ?? 0;
        var header = justLearned is null
            ? $"Biblioteca ensinada: {count} de {total} cartas."
            : $"Biblioteca ensinada: {count} de {total} cartas. Aprendeu agora: {justLearned}.";
        LearningStatusText.Text = diagnostic is null ? header : $"{header} [{diagnostic}]";
    }

    // Fusions carries the raw domain data (FusionFinder.FusionChain), not a WPF element — it is
    // built on the background thread inside CaptureAndReadCoreAsync, and WPF UIElements have
    // thread affinity (they must be created on the Dispatcher thread they will be used from).
    // Timer_Tick, back on the UI thread after the await, is what turns each chain into an actual
    // row via FusionRowBuilder.Build.
    private readonly record struct ReadResult(
        DrawingBitmap? Annotated, List<SlotReadingView>? Rows, List<FusionFinder.FusionChain>? Fusions,
        string? JustLearned, string? TeachDiagnostic, string? Error, bool Inactive);

    /// <summary>
    /// <see cref="Learned"/> is set only on the tick a teach actually commits.
    /// <see cref="Diagnostic"/> is set on every call, whether or not anything was learned — it
    /// exists purely so the "why isn't this teaching?" question has a visible answer on screen
    /// (see <see cref="UpdateLearningStatus"/>) instead of only silent success or silent no-op.
    /// That distinction is what let <see cref="TryTeachAsync"/> go back to swallowing every
    /// exception in one <c>catch</c> at the bottom without that being a dead end for debugging —
    /// the exception's own message becomes the diagnostic text instead of vanishing.
    /// </summary>
    private readonly record struct TeachAttempt(string? Learned, string Diagnostic);

    /// <summary>
    /// Runs the capture, recognition and (best-effort) teaching for one tick. Recognition
    /// happens off the UI thread via <see cref="Task.Run(Func{Task})"/>; the name panel's OCR
    /// is itself async, so this whole method is too rather than blocking a pool thread on it.
    /// </summary>
    private Task<ReadResult> CaptureAndReadAsync(NormRect handRegion, NormRect nameRegion) =>
        Task.Run(() => CaptureAndReadCoreAsync(handRegion, nameRegion));

    private async Task<ReadResult> CaptureAndReadCoreAsync(NormRect handRegion, NormRect nameRegion)
    {
        var bounds = ScreenCapture.WindowBounds(_targetWindow);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return new ReadResult(null, null, null, null, null, "A janela do emulador não está mais visível.", false);

        // We read pixels off the composited desktop: if the emulator is not the foreground
        // window, whatever got raised over it would be captured instead — another window, or
        // the desktop. Rather than capture that garbage, skip the tick entirely and leave the
        // last real frame on screen; the status line is what tells the user it is paused.
        if (!ScreenCapture.IsForeground(_targetWindow))
            return new ReadResult(null, null, null, null, null, null, true);

        using var full = ScreenCapture.CaptureRegion(bounds);
        var frameSize = new DrawingRectangle(DrawingPoint.Empty, full.Size);

        DrawingBitmap handCrop;
        try
        {
            handCrop = FrameCropper.Crop(full, handRegion.ToPixels(frameSize));
        }
        catch (ArgumentOutOfRangeException)
        {
            return new ReadResult(null, null, null, null, null,
                "A região da mão não cabe mais na janela — ela pode ter sido redimensionada.", false);
        }

        using (handCrop)
        {
            var readings = _reader!.Read(handCrop, HandLayout.Default);
            var annotated = PreviewAnnotator.Annotate(handCrop, readings);
            var rows = readings.Select(r => new SlotReadingView(r)).ToList();

            // Testing slice: use whatever the recogniser currently reports, confident or not,
            // so this table reflects the recognition pipeline as it actually behaves right now.
            var handIds = readings.Where(r => r.Card is not null).Select(r => r.Card!.Id).ToList();
            var fusions = _cards!.PossibleChains(handIds).ToList();

            var teach = await TryTeachAsync(full, frameSize, nameRegion, handCrop, readings);

            return new ReadResult(annotated, rows, fusions, teach.Learned, teach.Diagnostic, null, false);
        }
    }

    /// <summary>
    /// The "path 1 teaches path 2" step: read whatever name the game currently shows, work out
    /// which hand slot that refers to, and — only when both are unambiguous — save that slot's
    /// artwork as what this card looks like on this machine. Never throws; a failure here should
    /// not take down recognition, which is the part that actually matters every tick.
    ///
    /// Only bothers at all when the currently-selected slot is not already a confident read
    /// from the *taught* library specifically — not just confident from official-art matching.
    /// This distinction matters: official-art "confident" was measured to still be wrong
    /// sometimes (its score/margin thresholds were rough starting points, not validated
    /// accuracy), and skipping teaching whenever official art claims confidence would mean a
    /// card that official art confidently mis-reads can never get corrected — the exact case
    /// teaching exists to fix. Once a card really is backed by a taught template, though, that
    /// comparison is trustworthy (same rendering style on both sides), so it is fine to stop
    /// paying for OCR on it every tick.
    /// </summary>
    private async Task<TeachAttempt> TryTeachAsync(DrawingBitmap full, DrawingRectangle frameSize,
        NormRect nameRegion, DrawingBitmap handCrop, IReadOnlyList<SlotReading> readings)
    {
        try
        {
            // Cheap (a colour threshold, no OCR) — worth doing before anything expensive so an
            // already-taught-and-confident selected card can bail out without ever touching OCR.
            var selectedSlot = SelectionDetector.FindSelectedSlot(handCrop, HandLayout.Default.SlotCount);
            if (selectedSlot is not { } slotIndex)
                return new TeachAttempt(null, "nenhuma carta selecionada detectada (seta não encontrada)");

            var currentReading = readings.FirstOrDefault(r => r.Slot == slotIndex + 1);
            if (currentReading is { Verdict: SlotVerdict.Confident, Source: MatchSource.Taught })
                return new TeachAttempt(null, $"slot {slotIndex + 1} já confiante via ensinado, nada a fazer");

            using var namePanelCrop = FrameCropper.Crop(full, nameRegion.ToPixels(frameSize));
            var nameText = await NameReader.Read(namePanelCrop);

            var nameMatch = CardNameMatcher.Match(_cards!, nameText);
            if (!nameMatch.Confident)
                return new TeachAttempt(null, $"OCR leu \"{nameText}\" — sem correspondência de nome confiável");

            // Require this exact (slot, card) pairing to repeat before acting on it — see the
            // field comment on _pendingTeach for why.
            if (_pendingTeach is { } pending && pending.SlotIndex == slotIndex && pending.CardId == nameMatch.Card!.Id)
                _pendingTeach = pending with { Streak = pending.Streak + 1 };
            else
                _pendingTeach = (slotIndex, nameMatch.Card!.Id, 1);

            if (_pendingTeach.Value.Streak < TeachStreakRequired)
                return new TeachAttempt(null,
                    $"candidato slot {slotIndex + 1} = {nameMatch.Card!.Name} " +
                    $"({_pendingTeach.Value.Streak}/{TeachStreakRequired} ciclos seguidos)");

            var slotBounds = HandReader.SlotBounds(handCrop.Size, HandLayout.Default.SlotCount)[slotIndex];
            using var slotCrop = handCrop.Clone(slotBounds, handCrop.PixelFormat);
            if (CardArtLibrary.LooksEmpty(slotCrop))
                return new TeachAttempt(null, $"slot {slotIndex + 1} parece vazio (imagem sem detalhe) — não ensinado");

            // Teach only the artwork, not the ATK/DEF row below it — see HandLayout.ArtOnly for
            // why that row is worth trimming away rather than just going along for the ride.
            // HandReader.ReadSlot crops its own taught-library query the exact same way, so the
            // two sides of every future comparison stay in matching proportions.
            using var artOnlyCrop = slotCrop.Clone(HandLayout.ArtOnly(slotCrop.Size), slotCrop.PixelFormat);
            _taught!.Teach(nameMatch.Card!.Id, artOnlyCrop);
            var label = $"{nameMatch.Card.Name} (#{nameMatch.Card.Id})";
            return new TeachAttempt(label, $"ensinou agora: {label}");
        }
        catch (Exception ex)
        {
            return new TeachAttempt(null, $"ERRO ao tentar ensinar: {ex.Message}");
        }
    }

    private void Status(string text) => StatusText.Text = text;
}
