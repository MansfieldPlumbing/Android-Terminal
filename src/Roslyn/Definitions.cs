// =================================================================================================
// ForbiddenName
// =================================================================================================

/*
public sealed class TerminalEngine
private readonly TerminalEngine _engine;
*/

// FAIL:
// "Engine" supplies no mechanism, ownership, transformation, or lifetime. In this repository the
// noun is forbidden. Name the actual parser, state, renderer, runtime, scheduler, or other thing.


// =================================================================================================
// StaleIdentity
// =================================================================================================

/*
namespace Terminal.Engine;

<RootNamespace>Terminal.Engine</RootNamespace>
<ProjectReference Include="..\Terminal.Engine\Terminal.Engine.csproj" />
*/

// FAIL:
// The project has been renamed to Terminal.VT but its namespace / project identity still claims the
// dead name. A violent directory rename is not a completed architectural rename.


// =================================================================================================
// StaleReference
// =================================================================================================

/*
using Terminal.Engine;

<ProjectReference Include="..\Terminal.Engine\Terminal.Engine.csproj" />
*/

// FAIL:
// Another project still points at the retired identity. The rename is not complete until consumers
// address Terminal.VT and no source, project reference, assembly identity, or build path resurrects it.


// =================================================================================================
// MisplacedVtCode
// =================================================================================================

/*
namespace Terminal.Multiplexer;

private void ReflowPrimary(int columns, int rows) { ... }
private readonly List<ScrollbackLine> _scrollback = [];
TerminalCell cell = ...;
*/

// FAIL:
// Reflow, cells, screens, parser state, scrollback semantics, and Unicode display mechanics are VT
// semantics. Multiplexer transports route bytes and resize requests; it does not interpret them.


// =================================================================================================
// MisplacedVtArtifact
// =================================================================================================

/*
src\Terminal.Multiplexer\reflow.patch

diff --git a/src/Terminal.Engine/TerminalEngine.cs b/src/Terminal.Engine/TerminalEngine.cs
+    private void ReflowPrimary(int newColumns, int newRows)
*/

// FAIL:
// The patch changes terminal semantic state and reflow behavior but is stored with the Multiplexer.
// Move the work to Terminal.VT, retarget the dead Terminal.Engine path, then apply/rebuild it there.


// =================================================================================================
// PlatformLeak
// =================================================================================================

/*
using Android.Views;
using System.Runtime.InteropServices;

[LibraryImport("kernel32.dll")]
private static partial int ReadFile(...);
*/

// FAIL:
// Terminal.VT is platform-neutral terminal semantics. Android, Win32, libc, JNI, native handles, and
// platform resource lifetime terminate outside this project.


// =================================================================================================
// ProcessLeak
// =================================================================================================

/*
private IPtySession _session;
private SafeProcessHandle _process;
public uint ProcessId { get; }
*/

// FAIL:
// PTYs and child processes are Multiplexer ownership. VT consumes terminal input and produces terminal
// state / reply intent; it does not acquire or supervise the process producing the bytes.


// =================================================================================================
// TransportLeak
// =================================================================================================

/*
EndpointTransport endpoint;
WireFrame frame;
RouterMessage message;
ulong generation;
*/

// FAIL:
// Remedy framing, worker generation, route protocol, correlation, and channel transport are not VT
// semantics. VT must remain usable without knowing how bytes reached it.


// =================================================================================================
// ViewportState
// =================================================================================================

/*
private int _scrollbackOffset;

public void ScrollViewport(int rows)
{
    _scrollbackOffset = Math.Clamp(_scrollbackOffset + rows, 0, _scrollback.Count);
}
*/

// FAIL:
// Scrollback content may be terminal history. The user's current camera position into that history is
// presentation state. Touch/scroll navigation does not mutate VT semantic truth.


// =================================================================================================
// SelectionState
// =================================================================================================

/*
public TerminalSelection? Selection { get; private set; }
public void SetSelection(TerminalPoint anchor, TerminalPoint active) { ... }
public void ClearSelection() { ... }
*/

// FAIL:
// Selecting text changes what the user is interacting with, not what the terminal stream means.
// Selection belongs above Terminal.VT.


// =================================================================================================
// CompositionState
// =================================================================================================

/*
private string _composition = string.Empty;
private int _compositionCaret;

public void SetComposition(string? text, int caretIndex) { ... }
private TerminalCursor OverlayComposition(TerminalCell[][] lines, bool visible) { ... }
*/

// FAIL:
// IME composition is speculative user input that the PTY has not emitted. Projecting it into cloned
// terminal cells mixes presentation/input truth with actual terminal-output truth.


// =================================================================================================
// PresentationInvalidation
// =================================================================================================

/*
private readonly HashSet<int> _dirtyRows = [];
public event Action? Changed;
private void MarkDirty(int row) => _dirtyRows.Add(row);
*/

// FAIL:
// These members exist to tell a renderer when to repaint. Terminal semantic revision may be owned by
// VT; renderer invalidation bookkeeping is presentation policy and belongs above it.


// =================================================================================================
// ConsumptiveSnapshot
// =================================================================================================

/*
public TerminalSnapshot CaptureSnapshot(bool consumeDirtyRows = true)
{
    ...
    if (consumeDirtyRows) _dirtyRows.Clear();
    ...
}
*/

// FAIL:
// Observing terminal state consumes hidden notification state. Reader A therefore changes what reader
// B can observe. Snapshot capture should describe semantic state, not own repaint acknowledgement.


// =================================================================================================
// FullGridSnapshot
// =================================================================================================

/*
lines = CloneLines(_screen.Lines);
*/

// FAIL:
// Every semantic change causes a complete grid clone before presentation. This defeats the value of
// tracking change locality and places whole-screen allocation on the sustained-output path.


// =================================================================================================
// ParserHotPathAllocation
// =================================================================================================

/*
ProcessCsi(_sequence.ToString(), character);

string[] parts = sequence.Split(';');
var values = new int[parts.Length];
*/

// FAIL:
// CSI/OSC parsing is the steady-state byte/text path. Converting the sequence builder to strings and
// splitting into new arrays for each control sequence creates avoidable allocation in the parser.


// =================================================================================================
// UnboundedHyperlinks
// =================================================================================================

/*
int id = _nextHyperlink++;
_hyperlinks[id] = uri;
*/

// FAIL:
// Unique OSC 8 links accumulate for the lifetime of the terminal with no visible bound or eviction.
// Long-running terminal state must have a deliberate capacity / retirement rule.


// =================================================================================================
// IncompleteReset
// =================================================================================================

/*
private void Reset()
{
    _primary = new Screen(columns, rows);
    _alternate = new Screen(columns, rows);
    _scrollback.Clear();
    _activeHyperlink = 0;
    ResetRendition();
}
*/

// FAIL:
// Reset rebuilds visible screens but leaves other terminal-owned state alive: hyperlink registry / id
// generation, parser accumulation/state, and pending surrogate state. A reset must restore one known
// semantic starting state rather than only the most obvious fields.


// =================================================================================================
// PrimaryResizeWithoutReflow
// =================================================================================================

/*
_primary.Resize(columns, rows);
for (int i = 0; i < _scrollback.Count; i++)
    _scrollback[i] = ResizeLine(_scrollback[i], columns);
*/

// FAIL:
// Each physical row is truncated/copied independently, so the distinction between an autowrapped row
// and a real line ending is lost. Primary-screen resize must rebuild logical wrapped lines and carry
// cursor position through the same reflow. The alternate screen may remain a deliberate hard resize.


// =================================================================================================
// RawCellShift
// =================================================================================================

/*
Array.Copy(line, column, line, column + count, Columns - column - count);
Array.Copy(line, column + count, line, column, Columns - column - count);
*/

// FAIL:
// Raw array movement bypasses the representation invariant for wide presented characters. Insert /
// delete must move semantic cells through logic that cannot orphan or split their occupied columns.


// =================================================================================================
// ContinuationCell
// =================================================================================================

/*
public bool IsContinuation => DisplayWidth == 0;

_screen.Lines[row][column + 1] = new TerminalCell(
    string.Empty, 0, foreground, background, attributes, hyperlink);
*/

// FAIL:
// One cell is one presented character by eye. The second terminal column occupied by a wide character
// is addressing geometry, not a second fake semantic cell. Keep cell identity separate from columns.


// =================================================================================================
// SpecialistTermSurface
// =================================================================================================

/*
public readonly record struct TerminalCell(
    string Grapheme,
    byte DisplayWidth,
    ...);
*/

// FAIL:
// Grapheme is legitimate Unicode vocabulary, but it is mechanical vocabulary. The public cell model
// should say what Terminal means by eye (character/text + occupied columns); Unicode segmentation stays
// down beside rune/width/ANSI mechanics where the distinction is actually required.


// =================================================================================================
// UnboundedControlSequence
// =================================================================================================

/*
case ParserState.Csi:
    _sequence.Append(character);
    break;

case ParserState.Osc:
    _sequence.Append(character);
    break;
*/

// FAIL:
// Hostile terminal output can grow parser state without bound. Preserve explicit CSI/OSC accumulation
// ceilings and deterministic cancellation back to Ground when those bounds are exceeded.
