//
// TextEditor.cs
//
// Author:
//   Mike Krüger <mkrueger@novell.com>
//
// Copyright (C) 2007 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Mono.TextEditor.Highlighting;
using Mono.TextEditor.PopupWindow;
using Mono.TextEditor.Theatrics;

using Gdk;
using Gtk;

namespace Mono.TextEditor
{
	[System.ComponentModel.Category("Mono.TextEditor")]
	[System.ComponentModel.ToolboxItem(true)]
	public class TextEditor : Gtk.Widget, ITextEditorDataProvider
	{
		TextEditorData textEditorData;
		
		protected IconMargin       iconMargin;
		protected GutterMargin     gutterMargin;
		protected DashedLineMargin dashedLineMargin;
		protected FoldMarkerMargin foldMarkerMargin;
		protected TextViewMargin   textViewMargin;
		
		LineSegment longestLine      = null;
		int         longestLineWidth = -1;
		
		List<Margin> margins = new List<Margin> ();
		int oldRequest = -1;
		
		bool isDisposed = false;
		IMMulticontext imContext;
		Gdk.EventKey lastIMEvent;
		bool imContextActive;
		
		string currentStyleName;
		
	//	string currentModeStatus;
		
		// Tooltip fields
		const int TooltipTimer = 200;
		object tipItem;
		bool showTipScheduled;
		bool hideTipScheduled;
		int tipX, tipY;
		uint tipTimeoutId;
		Gtk.Window tipWindow;
		List<ITooltipProvider> tooltipProviders = new List<ITooltipProvider> ();
		ITooltipProvider currentTooltipProvider;
		double mx, my;
		
		public Document Document {
			get {
				return textEditorData.Document;
			}
			set {
				textEditorData.Document.TextReplaced -= OnDocumentStateChanged;
				textEditorData.Document.TextSet -= OnTextSet;
				textEditorData.Document = value;
				textEditorData.Document.TextReplaced += OnDocumentStateChanged;
				textEditorData.Document.TextSet += OnTextSet;
			}
		}
		
		public Mono.TextEditor.Caret Caret {
			get {
				return textEditorData.Caret;
			}
		}
		
		protected IMMulticontext IMContext {
			get { return imContext; }
		}
		
		public MenuItem CreateInputMethodMenuItem (string label)
		{
			MenuItem imContextMenuItem = new MenuItem (label);
			Menu imContextMenu = new Menu ();
			imContextMenuItem.Submenu = imContextMenu;
			IMContext.AppendMenuitems (imContextMenu);
			return imContextMenuItem;
		}
		
		public ITextEditorOptions Options {
			get {
				return textEditorData.Options;
			}
			set {
				if (textEditorData.Options != null)
					textEditorData.Options.Changed -= OptionsChanged;
				textEditorData.Options = value;
				textEditorData.Options.Changed += OptionsChanged;
				if (IsRealized)
					OptionsChanged (null, null);
			}
		}
		Dictionary<int, int> lineHeights = new Dictionary<int, int> ();
		
		public TextEditor () : this(new Document ())
		{
			textEditorData.Document.LineChanged += UpdateLinesOnTextMarkerHeightChange; 
		}
		
		Gdk.Pixmap buffer = null, flipBuffer = null;
		
		void DoFlipBuffer ()
		{
			Gdk.Pixmap tmp = buffer;
			buffer = flipBuffer;
			flipBuffer = tmp;
		}
		
		void DisposeBgBuffer ()
		{
			buffer = buffer.Kill ();
			flipBuffer = flipBuffer.Kill ();
		}
		
		void AllocateWindowBuffer (Rectangle allocation)
		{
			DisposeBgBuffer ();
			if (this.IsRealized) {
				buffer = new Gdk.Pixmap (this.GdkWindow, allocation.Width, allocation.Height);
				flipBuffer = new Gdk.Pixmap (this.GdkWindow, allocation.Width, allocation.Height);
			}
		}
		int oldHAdjustment = -1;
		void HAdjustmentValueChanged (object sender, EventArgs args)
		{
			if (this.textEditorData.HAdjustment.Value != System.Math.Ceiling (this.textEditorData.HAdjustment.Value)) {
				this.textEditorData.HAdjustment.Value = System.Math.Ceiling (this.textEditorData.HAdjustment.Value);
				return;
			}
/*			if (this.containerChildren.Count > 0)
				QueueResize ();*/
			HideTooltip ();
			textViewMargin.HideCodeSegmentPreviewWindow ();
			int curHAdjustment = (int)this.textEditorData.HAdjustment.Value;
			if (oldHAdjustment == curHAdjustment)
				return;
			
			this.RepaintArea (this.textViewMargin.XOffset, 0, this.Allocation.Width - this.textViewMargin.XOffset, this.Allocation.Height);
		}
		
		void VAdjustmentValueChanged (object sender, EventArgs args)
		{
			HideTooltip ();
			textViewMargin.HideCodeSegmentPreviewWindow ();
			
			if (buffer == null)
				AllocateWindowBuffer (this.Allocation);
			
			if (this.textEditorData.VAdjustment.Value != System.Math.Ceiling (this.textEditorData.VAdjustment.Value)) {
				this.textEditorData.VAdjustment.Value = System.Math.Ceiling (this.textEditorData.VAdjustment.Value);
				return;
			}

			if (isMouseTrapped)
				FireMotionEvent (mx + textViewMargin.XOffset, my, lastState);
			textViewMargin.VAdjustmentValueChanged ();
			
			int delta = (int)(this.textEditorData.VAdjustment.Value - this.oldVadjustment);
			oldVadjustment = this.textEditorData.VAdjustment.Value;
			
			// update pending redraws
			if (redrawList.Count > 0)
				redrawList = new List<Rectangle> (redrawList.Select (rectangle => { rectangle.Y -= delta; return rectangle;}));
			TextViewMargin.caretY -= delta;
			
			if (System.Math.Abs (delta) >= Allocation.Height - this.LineHeight * 2 || this.TextViewMargin.inSelectionDrag) {
				this.Repaint ();
				return;
			}
			int from, to;
			if (delta > 0) {
				from = delta;
				to   = 0;
			} else {
				from = 0;
				to   = -delta;
			}
			
			DoFlipBuffer ();
			this.buffer.DrawDrawable (Style.BackgroundGC (StateType.Normal), 
			                          this.flipBuffer,
			                          0, from, 
			                          0, to, 
			                          Allocation.Width, Allocation.Height - from - to);
			GdkWindow.Scroll (0, -delta);
			renderedLines.Clear ();
			if (delta > 0) {
				delta += LineHeight;
				RepaintArea (0, Allocation.Height - delta, Allocation.Width, delta);
			} else {
				delta -= LineHeight;
				RepaintArea (0, 0, Allocation.Width, -delta);
			}
			TextViewMargin.VAdjustmentValueChanged ();
		}
		
		protected override void OnSetScrollAdjustments (Adjustment hAdjustement, Adjustment vAdjustement)
		{
			if (textEditorData == null)
				return;
			if (textEditorData.HAdjustment != null)
				textEditorData.HAdjustment.ValueChanged -= HAdjustmentValueChanged;
			if (textEditorData.VAdjustment != null)
				textEditorData.VAdjustment.ValueChanged -= VAdjustmentValueChanged;
			
			this.textEditorData.HAdjustment = hAdjustement;
			this.textEditorData.VAdjustment = vAdjustement;
			
			if (hAdjustement == null || vAdjustement == null)
				return;

			this.textEditorData.HAdjustment.ValueChanged += HAdjustmentValueChanged;
			this.textEditorData.VAdjustment.ValueChanged += VAdjustmentValueChanged;
		}
		
		protected TextEditor (IntPtr raw) : base (raw)
		{
		}
		
		public TextEditor (Document doc)
			: this (doc, null)
		{
		}
		
		public TextEditor (Document doc, ITextEditorOptions options)
			: this (doc, options, new SimpleEditMode ())
		{
		}
		
		public TextEditor (Document doc, ITextEditorOptions options, EditMode initialMode)
		{
			textEditorData = new TextEditorData (doc);
			textEditorData.RecenterEditor += delegate {
				CenterToCaret ();
				StartCaretPulseAnimation ();
			};
			doc.TextReplaced += OnDocumentStateChanged;
			doc.TextSet += OnTextSet;

			textEditorData.CurrentMode = initialMode;
			
//			this.Events = EventMask.AllEventsMask;
			this.Events = EventMask.PointerMotionMask | EventMask.ButtonPressMask | EventMask.ButtonReleaseMask | EventMask.EnterNotifyMask | EventMask.LeaveNotifyMask | EventMask.VisibilityNotifyMask | EventMask.FocusChangeMask | EventMask.ScrollMask | EventMask.KeyPressMask | EventMask.KeyReleaseMask;
			this.DoubleBuffered = true;
			this.AppPaintable = false;
			base.CanFocus = true;
			this.RedrawOnAllocate = false;
			WidgetFlags |= WidgetFlags.NoWindow;
			iconMargin = new IconMargin (this);
			gutterMargin = new GutterMargin (this);
			dashedLineMargin = new DashedLineMargin (this);
			foldMarkerMargin = new FoldMarkerMargin (this);
			textViewMargin = new TextViewMargin (this);

			margins.Add (iconMargin);
			margins.Add (gutterMargin);
			margins.Add (dashedLineMargin);
			margins.Add (foldMarkerMargin);
			margins.Add (textViewMargin);
			this.textEditorData.SelectionChanged += TextEditorDataSelectionChanged;
			this.textEditorData.UpdateAdjustmentsRequested += TextEditorDatahandleUpdateAdjustmentsRequested;
			Document.DocumentUpdated += DocumentUpdatedHandler;
			
			this.textEditorData.Options = options ?? TextEditorOptions.DefaultOptions;
			this.textEditorData.Options.Changed += OptionsChanged;
			
			
			Gtk.TargetList list = new Gtk.TargetList ();
			list.AddTextTargets (ClipboardActions.CopyOperation.TextType);
			Gtk.Drag.DestSet (this, DestDefaults.All, (TargetEntry[])list, DragAction.Move | DragAction.Copy);
			
			imContext = new IMMulticontext ();
			imContext.Commit += IMCommit;
			
			imContext.UsePreedit = true;
			imContext.PreeditStart += delegate {
				preeditOffset = Caret.Offset;
				this.textViewMargin.ForceInvalidateLine (Caret.Line);
				this.textEditorData.Document.CommitLineUpdate (Caret.Line);
			};
			imContext.PreeditEnd += delegate {
				preeditOffset = -1;
				this.textViewMargin.ForceInvalidateLine (Caret.Line);
				this.textEditorData.Document.CommitLineUpdate (Caret.Line);
			};
			imContext.PreeditChanged += delegate(object sender, EventArgs e) {
				if (preeditOffset >= 0) {
					imContext.GetPreeditString (out preeditString, out preeditAttrs, out preeditCursorPos);
					this.textViewMargin.ForceInvalidateLine (Caret.Line);
					this.textEditorData.Document.CommitLineUpdate (Caret.Line);
				}
			};
			
			using (Pixmap inv = new Pixmap (null, 1, 1, 1)) {
				invisibleCursor = new Cursor (inv, inv, Gdk.Color.Zero, Gdk.Color.Zero, 0, 0);
			}
			
			InitAnimations ();
			this.Document.EndUndo += delegate {
				if (this.Document.HeightChanged) {
					this.Document.HeightChanged = false;
					SetAdjustments ();
				}
			};
		}

		void TextEditorDatahandleUpdateAdjustmentsRequested (object sender, EventArgs e)
		{
			SetAdjustments ();
		}
		
		
		public void ShowListWindow<T> (ListWindow<T> window, DocumentLocation loc)
		{
			Gdk.Point p = TextViewMargin.LocationToDisplayCoordinates (loc);
			int ox = 0, oy = 0;
			GdkWindow.GetOrigin (out ox, out oy);
	
			window.Move (ox + p.X - window.TextOffset , oy + p.Y + LineHeight);
			window.ShowAll ();
		}
		
		internal int preeditCursorPos = -1, preeditOffset = -1;
		internal string preeditString;
		internal Pango.AttrList preeditAttrs;
		
		void CaretPositionChanged (object sender, DocumentLocationEventArgs args) 
		{
			HideTooltip ();
			ResetIMContext ();
			
			if (Caret.AutoScrollToCaret)
				ScrollToCaret ();
			
//			Rectangle rectangle = textViewMargin.GetCaretRectangle (Caret.Mode);
			RequestResetCaretBlink ();
			
			if (!IsSomethingSelected) {
				if (/*Options.HighlightCaretLine && */args.Location.Line != Caret.Line) 
					RedrawMarginLine (TextViewMargin, args.Location.Line);
				RedrawMarginLine (TextViewMargin, Caret.Line);
			}
		}
		
		Selection oldSelection = null;
		void TextEditorDataSelectionChanged (object sender, EventArgs args)
		{
			if (IsSomethingSelected) {
				ClipboardActions.ClearPrimary ();
				ISegment selectionRange = MainSelection.GetSelectionRange (textEditorData);
				if (selectionRange.Offset >= 0 && selectionRange.EndOffset < Document.Length)
					ClipboardActions.CopyToPrimary (this.textEditorData);
			}
			// Handle redraw
			Selection selection = Selection.Clone (MainSelection);
			int startLine    = selection != null ? selection.Anchor.Line : -1;
			int endLine      = selection != null ? selection.Lead.Line : -1;
			int oldStartLine = oldSelection != null ? oldSelection.Anchor.Line : -1;
			int oldEndLine   = oldSelection != null ? oldSelection.Lead.Line : -1;
			if (SelectionMode == SelectionMode.Block) {
				this.RedrawMarginLines (this.textViewMargin, 
				                        System.Math.Min (System.Math.Min (oldStartLine, oldEndLine), System.Math.Min (startLine, endLine)),
				                        System.Math.Max (System.Math.Max (oldStartLine, oldEndLine), System.Math.Max (startLine, endLine)));
				oldSelection = selection;
			} else {
				if (endLine < 0 && startLine >=0)
					endLine = Document.LineCount;
				if (oldEndLine < 0 && oldStartLine >=0)
					oldEndLine = Document.LineCount;
				int from = oldEndLine, to = endLine;
				if (selection != null && oldSelection != null) {
					if (startLine != oldStartLine && endLine != oldEndLine) {
						from = System.Math.Min (startLine, oldStartLine);
						to   = System.Math.Max (endLine, oldEndLine);
					} else if (startLine != oldStartLine) {
						from = startLine;
						to   = oldStartLine;
					} else if (endLine != oldEndLine) {
						from = endLine;
						to   = oldEndLine;
					} else if (startLine == oldStartLine && endLine == oldEndLine)  {
						if (selection.Anchor == oldSelection.Anchor) {
							this.RedrawMarginLine (this.textViewMargin, endLine);
						} else if (selection.Lead == oldSelection.Lead) {
							this.RedrawMarginLine (this.textViewMargin, startLine);
						} else { // 3rd case - may happen when changed programmatically
							this.RedrawMarginLine (this.textViewMargin, endLine);
							this.RedrawMarginLine (this.textViewMargin, startLine);
						}
						from = to = -1;
					}
				} else {
					if (selection == null) {
						from = oldStartLine;
						to = oldEndLine;
					} else if (oldSelection == null) {
						from = startLine;
						to = endLine;
					} 
				}
				
				if (from >= 0 && to >= 0) {
					oldSelection = selection;
					this.RedrawMarginLines (this.textViewMargin, 
					                        System.Math.Max (0, System.Math.Min (from, to) - 1),
					                        System.Math.Max (from, to));
				}
			}
			OnSelectionChanged (EventArgs.Empty);
		}
		
		void ResetIMContext ()
		{
			if (imContextActive) {
				imContext.Reset ();
				imContextActive = false;
			}
		}
		
		void IMCommit (object sender, Gtk.CommitArgs ca)
		{
			try {
				if (IsRealized && IsFocus) {
					uint lastChar = Keyval.ToUnicode (lastIMEvent.KeyValue);
					
					//this, if anywhere, is where we should handle UCS4 conversions
					for (int i = 0; i < ca.Str.Length; i++) {
						int utf32Char;
						if (char.IsHighSurrogate (ca.Str, i)) {
							utf32Char = char.ConvertToUtf32 (ca.Str, i);
							i++;
						} else {
							utf32Char = (int) ca.Str[i];
						}
						
						//include the key & state if possible, i.e. if the char matches the unprocessed one
						if (lastChar == utf32Char)
							OnIMProcessedKeyPressEvent (lastIMEvent.Key, lastChar, lastIMEvent.State);
						else
							OnIMProcessedKeyPressEvent ((Gdk.Key)0, (uint)utf32Char, Gdk.ModifierType.None);
					}
				}
			} finally {
				ResetIMContext ();
			}
		}
		
		protected override bool OnFocusInEvent (EventFocus evnt)
		{
			IMContext.FocusIn ();
			RequestResetCaretBlink ();
			return base.OnFocusInEvent (evnt);
		}
		
		protected override bool OnFocusOutEvent (EventFocus evnt)
		{
			imContext.FocusOut ();
			GLib.Timeout.Add (10, delegate {
				// Don't immediately hide the tooltip. Wait a bit and check if the tooltip has the focus.
				if (tipWindow != null && !tipWindow.HasToplevelFocus)
					HideTooltip ();
				return false;
			});
			TextViewMargin.StopCaretThread ();
			return base.OnFocusOutEvent (evnt);
		}
		
		protected override void OnRealized ()
		{
			WidgetFlags |= WidgetFlags.Realized;
			WindowAttr attributes = new WindowAttr ();
			attributes.WindowType = Gdk.WindowType.Child;
			attributes.X = Allocation.X;
			attributes.Y = Allocation.Y;
			attributes.Width = Allocation.Width;
			attributes.Height = Allocation.Height;
			attributes.Wclass = WindowClass.InputOutput;
			attributes.Visual = this.Visual;
			attributes.Colormap = this.Colormap;
			attributes.EventMask = (int)(this.Events | Gdk.EventMask.ExposureMask);
			attributes.Mask = this.Events | Gdk.EventMask.ExposureMask;
//			attributes.Mask = EventMask;
			
			WindowAttributesType mask = WindowAttributesType.X | WindowAttributesType.Y | WindowAttributesType.Colormap | WindowAttributesType.Visual;
			this.GdkWindow = new Gdk.Window (ParentWindow, attributes, mask);
			this.GdkWindow.UserData = this.Raw;
			this.Style = Style.Attach (this.GdkWindow);
			this.WidgetFlags &= ~WidgetFlags.NoWindow;
			
			//base.OnRealized ();
			imContext.ClientWindow = this.GdkWindow;
			OptionsChanged (this, EventArgs.Empty);
			Caret.PositionChanged += CaretPositionChanged;
		}
		
		protected override void OnUnrealized ()
		{
			imContext.ClientWindow = null;
			DisposeTooltip ();
			if (showTipScheduled || hideTipScheduled) {
				RemoveTipTimer ();
				showTipScheduled = hideTipScheduled = false;
			}
			if (this.GdkWindow != null) {
				this.GdkWindow.UserData = IntPtr.Zero;
				this.GdkWindow.Destroy ();
				this.WidgetFlags |= WidgetFlags.NoWindow;
			}
			base.OnUnrealized ();
		}

		
		void DocumentUpdatedHandler (object sender, EventArgs args)
		{
			foreach (DocumentUpdateRequest request in Document.UpdateRequests) {
				request.Update (this);
			}
		}
		
		public event EventHandler EditorOptionsChanged;
		
		protected virtual void OptionsChanged (object sender, EventArgs args)
		{
			if (!this.IsRealized)
				return;
			if (EditorOptionsChanged != null)
				EditorOptionsChanged (this, args);
			if (currentStyleName != Options.ColorScheme) {
				currentStyleName = Options.ColorScheme;
				this.textEditorData.ColorStyle = Options.GetColorStyle (this.Style);
				SetWidgetBgFromStyle ();
			}
			
			iconMargin.IsVisible   = Options.ShowIconMargin;
			gutterMargin.IsVisible     = Options.ShowLineNumberMargin;
			dashedLineMargin.IsVisible = foldMarkerMargin.IsVisible = Options.ShowFoldMargin;
			
			foreach (Margin margin in this.margins) {
				margin.OptionsChanged ();
			}
			SetAdjustments (Allocation);
			this.QueueResize ();
			this.Repaint ();
		}
		
		void SetWidgetBgFromStyle ()
		{
			// This is a hack around a problem with repainting the drag widget.
			// When this is not set a white square is drawn when the drag widget is moved
			// when the bg color is differs from the color style bg color (e.g. oblivion style)
			if (this.textEditorData.ColorStyle != null && GdkWindow != null) {
				settingWidgetBg = true; //prevent infinite recusion
				
				this.ModifyBg (StateType.Normal, this.textEditorData.ColorStyle.Default.BackgroundColor);
				settingWidgetBg = false;
			}
		}
		
		bool settingWidgetBg = false;
		protected override void OnStyleSet (Gtk.Style previous_style)
		{
			base.OnStyleSet (previous_style);
			if (!settingWidgetBg && textEditorData.ColorStyle != null) {
				textEditorData.ColorStyle.UpdateFromGtkStyle (this.Style);
				SetWidgetBgFromStyle ();
			}
		}
 
		protected override void OnDestroyed ()
		{
			base.OnDestroyed ();
			
			if (isDisposed)
				return;
			this.isDisposed = true;
			
			RemoveScrollWindowTimer ();
			if (invisibleCursor != null) {
				invisibleCursor.Dispose ();
				invisibleCursor = null;
			}
			Caret.PositionChanged -= CaretPositionChanged;
			
			Document.DocumentUpdated -= DocumentUpdatedHandler;
			if (textEditorData.Options != null)
				textEditorData.Options.Changed -= OptionsChanged;

			imContext = imContext.Kill (x => x.Commit -= IMCommit);

			if (this.textEditorData.HAdjustment != null) {
				this.textEditorData.HAdjustment.ValueChanged -= HAdjustmentValueChanged;
				this.textEditorData.HAdjustment = null;
			}
			if (this.textEditorData.VAdjustment != null) {
				this.textEditorData.VAdjustment.ValueChanged -= VAdjustmentValueChanged;
				this.textEditorData.VAdjustment = null;
			}
			
			if (margins != null) {
				foreach (Margin margin in this.margins) {
					if (margin is IDisposable)
						((IDisposable)margin).Dispose ();
				}
				this.margins = null;
			}

			// Dispose the buffer after disposing the margins. Gdk will crash when trying to dispose
			// a drawable if a Win32 hdc is bound to it.
			DisposeBgBuffer ();
			
			iconMargin = null; 
			gutterMargin = null;
			dashedLineMargin = null;
			foldMarkerMargin = null;
			textViewMargin = null;
			this.textEditorData = this.textEditorData.Kill (x => x.SelectionChanged -= TextEditorDataSelectionChanged);
			this.Realized -= OptionsChanged;
			
		}
		
		internal void RedrawMargin (Margin margin)
		{
			if (isDisposed)
				return;
			this.RepaintArea (margin.XOffset, 0, GetMarginWidth (margin),  this.Allocation.Height);
		}
		
		public void RedrawMarginLine (Margin margin, int logicalLine)
		{
			if (isDisposed)
				return;
			
			this.RepaintArea (margin.XOffset, 
			                  LineToVisualY (logicalLine) - (int)this.textEditorData.VAdjustment.Value, 
			                  GetMarginWidth (margin), 
			                  GetLineHeight (logicalLine));
		}

		int GetMarginWidth (Margin margin)
		{
			if (margin.Width < 0)
				return Allocation.Width - margin.XOffset;
			return margin.Width;
		}

		
		internal void RedrawLine (int logicalLine)
		{
			if (isDisposed)
				return;
			this.RepaintArea (0, LineToVisualY (logicalLine) - (int)this.textEditorData.VAdjustment.Value,  this.Allocation.Width, GetLineHeight (logicalLine));
		}
		
		internal void RedrawPosition (int logicalLine, int logicalColumn)
		{
			if (isDisposed)
				return;
//				Console.WriteLine ("Redraw position: logicalLine={0}, logicalColumn={1}", logicalLine, logicalColumn);
			RedrawLine (logicalLine);
		}
		
		public void RedrawMarginLines (Margin margin, int start, int end)
		{
			if (isDisposed)
				return;
			if (start < 0)
				start = 0;
			int visualStart = (int)-this.textEditorData.VAdjustment.Value + LineToVisualY (start);
			if (end < 0)
				end = Document.LineCount - 1;
			int visualEnd   = (int)-this.textEditorData.VAdjustment.Value + LineToVisualY (end) + GetLineHeight (end);
			this.RepaintArea (margin.XOffset, visualStart, GetMarginWidth (margin), visualEnd - visualStart );
		}
			
		internal void RedrawLines (int start, int end)
		{
//			Console.WriteLine ("redraw lines: start={0}, end={1}", start, end);
			if (isDisposed)
				return;
			if (start < 0)
				start = 0;
			int visualStart = (int)-this.textEditorData.VAdjustment.Value + Document.LogicalToVisualLine (start) * LineHeight;
			if (end < 0)
				end = Document.LineCount - 1;
			int visualEnd   = (int)-this.textEditorData.VAdjustment.Value + Document.LogicalToVisualLine (end) * LineHeight + LineHeight;
			this.RepaintArea (0, visualStart, this.Allocation.Width, visualEnd - visualStart );
		}
		
		public void RedrawFromLine (int logicalLine)
		{
//			Console.WriteLine ("Redraw from line: logicalLine={0}", logicalLine);
			if (isDisposed)
				return;
			this.RepaintArea (0, (int)-this.textEditorData.VAdjustment.Value + LineToVisualY (logicalLine) , this.Allocation.Width, this.Allocation.Height);
		}
		
		public void RunAction (Action<TextEditorData> action)
		{
			try {
				action (this.textEditorData);
			} catch (Exception e) {
				Console.WriteLine ("Error while executing " + action + " :" + e);
			}
		}
		
		public void SimulateKeyPress (Gdk.Key key, uint unicodeChar, ModifierType modifier)
		{
			ModifierType filteredModifiers = modifier & (ModifierType.ShiftMask | ModifierType.Mod1Mask | ModifierType.ControlMask | ModifierType.MetaMask | ModifierType.SuperMask);
			
			ModifierType modifiersThatPermitChars = ModifierType.ShiftMask;
			if (Platform.IsMac)
				modifiersThatPermitChars |= ModifierType.Mod1Mask;
			
			if ((filteredModifiers & ~modifiersThatPermitChars) != 0)
				unicodeChar = 0;
			
			CurrentMode.InternalHandleKeypress (this, textEditorData, key, unicodeChar, filteredModifiers);
			RequestResetCaretBlink ();
		}
		
		bool IMFilterKeyPress (Gdk.EventKey evt)
		{
			if (lastIMEvent == evt)
				return false;
			
			if (evt.Type == EventType.KeyPress)
				lastIMEvent = evt;
			
			if (imContext.FilterKeypress (evt)) {
				imContextActive = true;
				return true;
			} else {
				return false;
			}
		}
		
		Gdk.Cursor invisibleCursor;
		
		internal void HideMouseCursor ()
		{
			GdkWindow.Cursor = invisibleCursor;
		}
		
		protected override bool OnKeyPressEvent (Gdk.EventKey evt)
		{
			ModifierType mod;
			Gdk.Key key;
			Platform.MapRawKeys (evt, out key, out mod);
			
			if (key == Gdk.Key.F1 && (mod & (ModifierType.ControlMask | ModifierType.ShiftMask)) == ModifierType.ControlMask) {
				Point p = textViewMargin.LocationToDisplayCoordinates (Caret.Location);
				ShowTooltip (Gdk.ModifierType.None, Caret.Offset, p.X, p.Y);
				return true;
			}
			
			if (key == Gdk.Key.space && (mod & (ModifierType.ShiftMask)) == ModifierType.ShiftMask && textViewMargin.IsCodeSegmentPreviewWindowShown) {
				textViewMargin.OpenCodeSegmentEditor ();
				return true;
			}
			
			uint unicodeChar = Gdk.Keyval.ToUnicode (evt.KeyValue);
			if (CurrentMode.WantsToPreemptIM || CurrentMode.PreemptIM (key, unicodeChar, mod)) {
				ResetIMContext ();	
				SimulateKeyPress (key, unicodeChar, mod);
				return true;
			}
			bool filter = IMFilterKeyPress (evt);
			if (!filter) {
				return OnIMProcessedKeyPressEvent (key, unicodeChar, mod);
			}
			return base.OnKeyPressEvent (evt);
		}
		
		/// <<remarks>
		/// The Key may be null if it has been handled by the IMContext. In such cases, the char is the value.
		/// </remarks>
		protected virtual bool OnIMProcessedKeyPressEvent (Gdk.Key key, uint ch, Gdk.ModifierType state)
		{
			SimulateKeyPress (key, ch, state);
			return true;
		}
		
		protected override bool OnKeyReleaseEvent (EventKey evnt)
		{
			if (IMFilterKeyPress (evnt))
				imContextActive = true;
			return true;
		}
		
		int mouseButtonPressed = 0;
		uint lastTime;
		int  pressPositionX, pressPositionY;
		protected override bool OnButtonPressEvent (Gdk.EventButton e)
		{
			pressPositionX = (int)e.X;
			pressPositionY = (int)e.Y;
			base.IsFocus = true;
			if (lastTime != e.Time) {// filter double clicks
				if (e.Type == EventType.TwoButtonPress) {
				    lastTime = e.Time;
				} else {
					lastTime = 0;
				}
				mouseButtonPressed = (int) e.Button;
				int startPos;
				Margin margin = GetMarginAtX ((int)e.X, out startPos);
				if (margin != null) {
					margin.MousePressed (new MarginMouseEventArgs (this, (int)e.Button, (int)(e.X - startPos), (int)e.Y, e.Type, e.State));
				}
			}
			return base.OnButtonPressEvent (e);
		}
	/*	protected override bool OnWidgetEvent (Event evnt)
		{
			Console.WriteLine (evnt.Type);
			return base.OnWidgetEvent (evnt);
		}*/
		
		public Margin LockedMargin {
			get;
			set;
		}
		
		Margin GetMarginAtX (int x, out int startingPos)
		{
			int curX = 0;
			foreach (Margin margin in this.margins) {
				if (!margin.IsVisible)
					continue;
				if (LockedMargin != null) {
					if (LockedMargin == margin) {
						startingPos = curX;
						return margin;
					}
				} else {
					if (curX <= x && (x <= curX + margin.Width || margin.Width < 0)) {
						startingPos = curX;
						return margin;
					}
				}
				curX += margin.Width;
			}
			startingPos = -1;
			return null;
		}
		
		protected override bool OnButtonReleaseEvent (EventButton e)
		{
			RemoveScrollWindowTimer ();
			int startPos;
			Margin margin = GetMarginAtX ((int)e.X, out startPos);
			if (margin != null)
				margin.MouseReleased (new MarginMouseEventArgs (this, (int)e.Button, (int)(e.X - startPos), (int)e.Y, EventType.ButtonRelease, e.State));
			ResetMouseState ();
			return base.OnButtonReleaseEvent (e);
		}
		
		protected void ResetMouseState ()
		{
			mouseButtonPressed = 0;
			textViewMargin.inDrag = false;
			textViewMargin.inSelectionDrag = false;
		}
		
		bool dragOver = false;
		ClipboardActions.CopyOperation dragContents = null;
		DocumentLocation defaultCaretPos, dragCaretPos;
		Selection selection = null;
		
		public bool IsInDrag {
			get {
				return dragOver;
			}
		}
		
		public void CaretToDragCaretPosition ()
		{
			Caret.Location = defaultCaretPos = dragCaretPos;
		}
		
		protected override void OnDragLeave (DragContext context, uint time_)
		{
			if (dragOver) {
				Caret.PreserveSelection = true;
				Caret.Location = defaultCaretPos;
				Caret.PreserveSelection = false;
				dragOver = false;
			}
			base.OnDragLeave (context, time_);
		}
		
		protected override void OnDragDataGet (DragContext context, SelectionData selection_data, uint info, uint time_)
		{
			if (this.dragContents != null) {
				this.dragContents.SetData (selection_data, info);
				this.dragContents = null;
			}
			base.OnDragDataGet (context, selection_data, info, time_);
		}
				
		protected override void OnDragDataReceived (DragContext context, int x, int y, SelectionData selection_data, uint info, uint time_)
		{
			textEditorData.Document.BeginAtomicUndo ();
			int dragOffset = Document.LocationToOffset (dragCaretPos);
			if (context.Action == DragAction.Move) {
				if (CanEdit (Caret.Line) && selection != null) {
					ISegment selectionRange = selection.GetSelectionRange (textEditorData);
					if (selectionRange.Offset < dragOffset)
						dragOffset -= selectionRange.Length;
					Caret.PreserveSelection = true;
					textEditorData.DeleteSelection (selection);
					Caret.PreserveSelection = false;

					if (this.textEditorData.IsSomethingSelected && selection.GetSelectionRange (textEditorData).Offset <= this.textEditorData.SelectionRange.Offset) {
						this.textEditorData.SelectionRange = new Segment (this.textEditorData.SelectionRange.Offset - selection.GetSelectionRange (textEditorData).Length, this.textEditorData.SelectionRange.Length);
						this.textEditorData.SelectionMode = selection.SelectionMode;
					}
					selection = null;
				}
			}
			if (selection_data.Length > 0 && selection_data.Format == 8) {
				Caret.Offset = dragOffset;
				if (CanEdit (dragCaretPos.Line)) {
					int offset = Caret.Offset;
					if (selection != null && selection.GetSelectionRange (textEditorData).Offset >= offset)
						selection = new Selection (Document.OffsetToLocation (selection.GetSelectionRange (textEditorData).Offset + selection_data.Text.Length), Document.OffsetToLocation (selection.GetSelectionRange (textEditorData).Offset + selection_data.Text.Length + selection.GetSelectionRange (textEditorData).Length));
					textEditorData.Insert (offset, selection_data.Text);
					Caret.Offset = offset + selection_data.Text.Length;
					MainSelection = new Selection (Document.OffsetToLocation (offset), Document.OffsetToLocation (offset + selection_data.Text.Length));
					textEditorData.PasteText (offset, selection_data.Text);
				}
				dragOver = false;
				context = null;
			}
			textEditorData.Document.EndAtomicUndo ();
			base.OnDragDataReceived (context, x, y, selection_data, info, time_);
		}
		
		protected override bool OnDragMotion (DragContext context, int x, int y, uint time)
		{
			if (!this.HasFocus)
				this.GrabFocus ();
			if (!dragOver) {
				defaultCaretPos = Caret.Location;
			}
			
			DocumentLocation oldLocation = Caret.Location;
			dragOver = true;
			Caret.PreserveSelection = true;
			dragCaretPos = VisualToDocumentLocation (x - textViewMargin.XOffset, y);
			int offset = Document.LocationToOffset (dragCaretPos);
			if (selection != null && offset >= this.selection.GetSelectionRange (textEditorData).Offset && offset < this.selection.GetSelectionRange (textEditorData).EndOffset) {
				Gdk.Drag.Status (context, DragAction.Default, time);
				Caret.Location = defaultCaretPos;
			} else {
				Gdk.Drag.Status (context, (context.Actions & DragAction.Move) == DragAction.Move ? DragAction.Move : DragAction.Copy, time);
				Caret.Location = dragCaretPos; 
			}
			this.RedrawLine (oldLocation.Line);
			if (oldLocation.Line != Caret.Line)
				this.RedrawLine (Caret.Line);
			Caret.PreserveSelection = false;
			return base.OnDragMotion (context, x, y, time);
		}
		
		Margin oldMargin = null;
		uint   scrollWindowTimer = 0;
		protected override bool OnMotionNotifyEvent (Gdk.EventMotion e)
		{
			RemoveScrollWindowTimer ();
			double x = e.X;
			double y = e.Y;
			Gdk.ModifierType mod = e.State;
			int startPos;
			Margin margin = GetMarginAtX ((int)x, out startPos);
			if (textViewMargin.inDrag && margin == this.textViewMargin && Gtk.Drag.CheckThreshold (this, pressPositionX, pressPositionY, (int)x, (int)y)) {
				dragContents = new ClipboardActions.CopyOperation ();
				dragContents.CopyData (textEditorData);
				DragContext context = Gtk.Drag.Begin (this, ClipboardActions.CopyOperation.targetList, DragAction.Move | DragAction.Copy, 1, e);
				CodeSegmentPreviewWindow window = new CodeSegmentPreviewWindow (this, true, textEditorData.SelectionRange, 300, 300);

				Gtk.Drag.SetIconWidget (context, window, 0, 0);
				selection = Selection.Clone (MainSelection);
				textViewMargin.inDrag = false;
			} else {
				FireMotionEvent (x, y, mod);
				if (mouseButtonPressed != 0) {
					RemoveScrollWindowTimer ();
					scrollWindowTimer = GLib.Timeout.Add (50, delegate {
						FireMotionEvent (x, y, mod);
						return true;
					});
				}
			}
			return base.OnMotionNotifyEvent (e);
		}
		
		void RemoveScrollWindowTimer ()
		{
			if (scrollWindowTimer != 0) {
				GLib.Source.Remove (scrollWindowTimer);
				scrollWindowTimer = 0;
			}
		}
		
		void RemoveTipTimer ()
		{
			if (tipTimeoutId != 0) {
				GLib.Source.Remove (tipTimeoutId);
				tipTimeoutId = 0;
			}
		}
		
		Gdk.ModifierType lastState = ModifierType.None;
		void FireMotionEvent (double x, double y, Gdk.ModifierType state)
		{
			lastState = state;
			mx = x - textViewMargin.XOffset;
			my = y;

			UpdateTooltip (state);

			int startPos;
			Margin margin;
			if (textViewMargin.inSelectionDrag) {
				margin = textViewMargin;
				startPos = textViewMargin.XOffset;
			} else {
				margin = GetMarginAtX ((int)x, out startPos);
				if (margin != null)
					GdkWindow.Cursor = margin.MarginCursor;
			}

			if (oldMargin != margin && oldMargin != null)
				oldMargin.MouseLeft ();
			
			if (margin != null) 
				margin.MouseHover (new MarginMouseEventArgs (this, mouseButtonPressed, (int)(x - startPos), (int)y, EventType.MotionNotify, state));
			oldMargin = margin;
		}

		#region CustomDrag (for getting dnd data from toolbox items for example)
		string     customText;
		Gtk.Widget customSource;
		public void BeginDrag (string text, Gtk.Widget source, DragContext context)
		{
			customText = text;
			customSource = source;
			source.DragDataGet += CustomDragDataGet;
			source.DragEnd     += CustomDragEnd;
		}
		void CustomDragDataGet (object sender, Gtk.DragDataGetArgs args) 
		{
			args.SelectionData.Text = customText;
		}
		void CustomDragEnd (object sender, Gtk.DragEndArgs args) 
		{
			customSource.DragDataGet -= CustomDragDataGet;
			customSource.DragEnd -= CustomDragEnd;
			customSource = null;
			customText = null;
		}
		#endregion
		bool isMouseTrapped = false;
		
		protected override bool OnEnterNotifyEvent (EventCrossing evnt)
		{
			isMouseTrapped = true;
			return base.OnEnterNotifyEvent (evnt);
		}
		
		protected override bool OnLeaveNotifyEvent (Gdk.EventCrossing e)
		{
			isMouseTrapped = false;
			if (tipWindow != null && currentTooltipProvider.IsInteractive (this, tipWindow))
				DelayedHideTooltip ();
			else
				HideTooltip ();
			
			textViewMargin.HideCodeSegmentPreviewWindow ();
			
			if (e.Mode == CrossingMode.Normal) {
				GdkWindow.Cursor = null;
				if (oldMargin != null)
					oldMargin.MouseLeft ();
			}
			return base.OnLeaveNotifyEvent (e); 
		}

		public int LineHeight {
			get {
				if (this.textViewMargin == null)
					return 16;
				return this.textViewMargin.LineHeight;
			}
		}
		
		public TextViewMargin TextViewMargin {
			get {
				return textViewMargin;
			}
		}
		
		public Margin IconMargin {
			get { return iconMargin; }
		}
		
		public Gdk.Point DocumentToVisualLocation (DocumentLocation loc)
		{
			Gdk.Point result = new Point ();
			LineSegment lineSegment = Document.GetLine (loc.Line);
			result.X = textViewMargin.ColumnToVisualX (lineSegment, loc.Column);
			result.Y = LineToVisualY (loc.Line);
			return result;
		}
		
		public DocumentLocation VisualToDocumentLocation (int x, int y)
		{
			return this.textViewMargin.VisualToDocumentLocation (x, y);
		}
		
		public DocumentLocation LogicalToVisualLocation (DocumentLocation location)
		{
			return Document.LogicalToVisualLocation (this.textEditorData, location);
		}
		
		public void CenterToCaret ()
		{
			if (isDisposed || Caret.Line < 0 || Caret.Line >= Document.LineCount)
				return;
			SetAdjustments (this.Allocation);
			//			Adjustment adj;
			//adj.Upper
			if (this.textEditorData.VAdjustment.Upper < Allocation.Height) {
				this.textEditorData.VAdjustment.Value = 0;
				return;
			}
			
			//	int yMargin = 1 * this.LineHeight;
			int caretPosition = LineToVisualY (Caret.Line);
			this.textEditorData.VAdjustment.Value = caretPosition - this.textEditorData.VAdjustment.PageSize / 2;
			
			if (this.textEditorData.HAdjustment.Upper < Allocation.Width)  {
				this.textEditorData.HAdjustment.Value = 0;
			} else {
				int caretX = textViewMargin.ColumnToVisualX (Document.GetLine (Caret.Line), Caret.Column);
				int textWith = Allocation.Width - textViewMargin.XOffset;
				if (this.textEditorData.HAdjustment.Value > caretX) {
					this.textEditorData.HAdjustment.Value = caretX;
				} else if (this.textEditorData.HAdjustment.Value + textWith < caretX + TextViewMargin.CharWidth) {
					int adjustment = System.Math.Max (0, caretX - textWith + TextViewMargin.CharWidth);
					this.textEditorData.HAdjustment.Value = adjustment;
				}
			}
		}
		bool inCaretScroll = false;
		public void ScrollToCaret ()
		{
			if (isDisposed || Caret.Line < 0 || Caret.Line >= Document.LineCount || inCaretScroll)
				return;
			inCaretScroll = true;
			try {
				UpdateAdjustments ();
				if (this.textEditorData.VAdjustment.Upper < Allocation.Height) {
					this.textEditorData.VAdjustment.Value = 0;
				} else {
					int yMargin = 1 * this.LineHeight;
					int caretPosition = LineToVisualY (Caret.Line);
					if (this.textEditorData.VAdjustment.Value > caretPosition) {
						this.textEditorData.VAdjustment.Value = caretPosition;
					} else if (this.textEditorData.VAdjustment.Value + this.textEditorData.VAdjustment.PageSize - this.LineHeight < caretPosition + yMargin) {
						this.textEditorData.VAdjustment.Value = caretPosition - this.textEditorData.VAdjustment.PageSize + this.LineHeight + yMargin;
					}
				}
				
				if (this.textEditorData.HAdjustment.Upper < Allocation.Width)  {
					this.textEditorData.HAdjustment.Value = 0;
				} else {
					int caretX = textViewMargin.ColumnToVisualX (Document.GetLine (Caret.Line), Caret.Column);
					int textWith = Allocation.Width - textViewMargin.XOffset;
					if (this.textEditorData.HAdjustment.Value > caretX) {
						this.textEditorData.HAdjustment.Value = caretX;
					} else if (this.textEditorData.HAdjustment.Value + textWith < caretX + TextViewMargin.CharWidth) {
						int adjustment = System.Math.Max (0, caretX - textWith + TextViewMargin.CharWidth);
						this.textEditorData.HAdjustment.Value = adjustment;
					}
				}
			} finally {
				inCaretScroll = false;
			}
		}
		
		protected override void OnSizeAllocated (Gdk.Rectangle allocation)
		{
			base.OnSizeAllocated (allocation);
/*			if (longestLine == null) {
				foreach (LineSegment line in Document.Lines) {
					if (longestLine == null || line.EditableLength > longestLine.EditableLength)
						longestLine = line;
				}
			}*/
			if (IsRealized)
				AllocateWindowBuffer (Allocation);
			if (this.GdkWindow != null) 
				this.GdkWindow.MoveResize (allocation);
			SetAdjustments (Allocation);
			Repaint ();
			textViewMargin.SetClip ();
		}
		
		protected override void OnMapped ()
		{
			if (buffer == null) {
				AllocateWindowBuffer (this.Allocation);
				if (Allocation.Width != 1 || Allocation.Height != 1)
					Repaint ();
			}
			base.OnMapped (); 
		}

		protected override void OnUnmapped ()
		{
			DisposeBgBuffer ();
			base.OnUnmapped (); 
		}
		
		protected override bool OnScrollEvent (EventScroll evnt)
		{
			var modifier = Platform.IsMac? Gdk.ModifierType.MetaMask : Gdk.ModifierType.ControlMask;
			if ((evnt.State & modifier) !=0) {
				if (evnt.Direction == ScrollDirection.Down)
					Options.ZoomIn ();
				else
					Options.ZoomOut ();
				this.Repaint ();
				if (isMouseTrapped)
					FireMotionEvent (mx + textViewMargin.XOffset, my, lastState);
				return true;
			}
			return base.OnScrollEvent (evnt); 
		}
		
		void SetHAdjustment ()
		{
			if (textEditorData.HAdjustment == null)
				return;
			textEditorData.HAdjustment.ValueChanged -= HAdjustmentValueChanged;
			if (longestLine != null && this.textEditorData.HAdjustment != null) {
				int maxX = longestLineWidth;
				if (maxX > Allocation.Width)
					maxX += 2 * this.textViewMargin.CharWidth;
				int width = Allocation.Width - this.TextViewMargin.XOffset;
				this.textEditorData.HAdjustment.SetBounds (0, maxX, this.textViewMargin.CharWidth, width, width);
				if (maxX < width)
					this.textEditorData.HAdjustment.Value = 0;
			}
			textEditorData.HAdjustment.ValueChanged += HAdjustmentValueChanged;
		}
		
		internal void SetAdjustments ()
		{
			SetAdjustments (Allocation);
		}
		
		internal void SetAdjustments (Gdk.Rectangle allocation)
		{
			if (this.textEditorData.VAdjustment != null) {
				int maxY = LineToVisualY (Document.LineCount - 1);
				if (maxY > allocation.Height)
					maxY += 5 * this.LineHeight;
				this.textEditorData.VAdjustment.SetBounds (0, 
				                                           maxY, 
				                                           LineHeight,
				                                           allocation.Height,
				                                           allocation.Height);
				if (maxY < allocation.Height)
					this.textEditorData.VAdjustment.Value = 0;
			}
			SetHAdjustment ();
		}
		
		public int GetWidth (string text)
		{
			return this.textViewMargin.GetWidth (text);
		}
		
		
		Dictionary<Margin, HashSet<int>> renderedLines = new Dictionary<Margin, HashSet<int>> ();
		
		void RenderMargins (Gdk.Drawable win, Gdk.Rectangle area)
		{
			this.TextViewMargin.rulerX = Options.RulerColumn * this.TextViewMargin.CharWidth - (int)this.textEditorData.HAdjustment.Value;
			int reminder  = (int)this.textEditorData.VAdjustment.Value % LineHeight;
//			int firstLine = CalculateLineNumber ((int)this.textEditorData.VAdjustment.Value);
			int startLine = CalculateLineNumber (area.Top - reminder + (int)this.textEditorData.VAdjustment.Value);
		//	int endLine   = CalculateLineNumber (area.Bottom + reminder + (int)this.textEditorData.VAdjustment.Value) - 1;
			
	//		if ((area.Bottom + reminder) % this.LineHeight != 0)
	//			endLine++;
			// Initialize the rendering of the margins. Determine wether each margin has to be
			// rendered or not and calculate the X offset.
			List<Margin> marginsToRender = new List<Margin> (this.margins.Count);
			int curX = 0;
			foreach (Margin margin in this.margins) {
				if (margin.IsVisible) {
					margin.XOffset = curX;
					if (curX >= area.X || margin.Width < 0) {
						margin.BeginRender (win, area, margin.XOffset);
						marginsToRender.Add (margin);
					}
					curX += margin.Width;
					if (!renderedLines.ContainsKey (margin))
						renderedLines.Add (margin, new HashSet<int> ());
				}
			}
			
			int startY = LineToVisualY (startLine);
			int curY = startY - (int)this.textEditorData.VAdjustment.Value;
			bool setLongestLine = false;
			//Console.WriteLine ("Render margins: startLine={0}, curY={1}, endY={2}", startLine, curY, area.Bottom);
			for (int visualLineNumber = startLine; ; visualLineNumber++) {
				//Console.WriteLine ("Render:" + visualLineNumber + " at " + curY);
				int logicalLineNumber = visualLineNumber;
				int lineHeight        = GetLineHeight (logicalLineNumber);
				LineSegment line = Document.GetLine (logicalLineNumber);
				int lastFold = -1;
				foreach (FoldSegment fs in Document.GetStartFoldings (line).Where (fs => fs.IsFolded)) {
					lastFold = System.Math.Max (fs.EndOffset, lastFold);
				}
				if (lastFold > 0) 
					visualLineNumber = Document.OffsetToLineNumber (lastFold);
				foreach (Margin margin in marginsToRender) {
					HashSet<int> linesAlreadyRendered = renderedLines[margin];
					if (linesAlreadyRendered.Contains (logicalLineNumber))
						continue;
					linesAlreadyRendered.Add (logicalLineNumber);
/*					if (margin.Width > 0 && area.Left > margin.XOffset + margin.Width)
						continue;
					if (area.Right <= margin.XOffset)
						break;*/
					//Console.WriteLine ("Render margin :" + margin);
					try {
						margin.Draw (win, area, logicalLineNumber, margin.XOffset, curY, lineHeight);
					} catch (Exception e) {
						System.Console.WriteLine (e);
					}
				}
				// take the line real render width from the text view margin rendering (a line can consist of more than 
				// one line and be longer (foldings!) ex. : someLine1[...]someLine2[...]someLine3)
				int lineWidth = textViewMargin.lastLineRenderWidth + (int)HAdjustment.Value;
				if (longestLine == null || lineWidth > longestLineWidth) {
					longestLine = line;
					longestLineWidth = lineWidth;
					setLongestLine = true;
				}
				curY += lineHeight;
				if (curY > area.Bottom)
					break;
			}
			
			if (setLongestLine) 
				SetHAdjustment ();

			foreach (Margin margin in marginsToRender)
				margin.EndRender (win, area, margin.XOffset);
		}
		/*
		protected override bool OnWidgetEvent (Event evnt)
		{
			System.Console.WriteLine(evnt);
			return base.OnWidgetEvent (evnt);
		}*/
		
		double oldVadjustment = 0;
		
		void UpdateAdjustments ()
		{
			int lastVisibleLine = Document.LogicalToVisualLine (Document.LineCount - 1);
			if (oldRequest != lastVisibleLine) {
				SetAdjustments (this.Allocation);
				oldRequest = lastVisibleLine;
			}
		}
		
		List<Gdk.Rectangle> redrawList = new List<Gdk.Rectangle> ();

		public void RepaintArea (int x, int y, int width, int height)
		{
			if (this.buffer == null)
				return;
//			Console.WriteLine ("RepaintArea: x={0}, y={1}, width={2}, height={3}", x, y, width, height);
//			Console.WriteLine (Environment.StackTrace);
			y = System.Math.Max (0, y);
			height = System.Math.Min (height, Allocation.Height - y);

			x = System.Math.Max (0, x);
			width = System.Math.Min (width, Allocation.Width - x);
			if (height < 0 || width < 0)
				return;
			
			lock (redrawList) {
				redrawList.Add (new Gdk.Rectangle (x, y, width, height));
			}
			
			QueueDrawArea (x, y, width, height);
		}
		
		public void Repaint ()
		{
			if (buffer == null)
				return;
//			Console.WriteLine ("REPAINT");
//			Console.WriteLine (Environment.StackTrace);
			lock (redrawList) {
				redrawList.Clear ();
				redrawList.Add (new Gdk.Rectangle (0, 0, this.Allocation.Width, this.Allocation.Height));
			}
			QueueDraw ();
		}
		
		protected override bool OnExposeEvent (Gdk.EventExpose e)
		{
			if (this.isDisposed)
				return true;
//			Console.WriteLine ("Expose:" + e.Area);
			UpdateAdjustments ();
			RenderPendingUpdates (e.Window);
			e.Window.DrawDrawable (Style.BackgroundGC (StateType.Normal), 
			                       buffer,
			                       e.Area.X, e.Area.Y, e.Area.X, e.Area.Y,
			                       e.Area.Width, e.Area.Height + 1);
			if (requestResetCaretBlink) {
				textViewMargin.ResetCaretBlink ();
				requestResetCaretBlink = false;
			}
			
			foreach (Animation animation in actors) {
				animation.Drawer.Draw (e.Window);
			}
			
			if (e.Area.Contains (TextViewMargin.caretX, TextViewMargin.caretY))
				textViewMargin.DrawCaret (e.Window);
			return true;
		}

		void RenderPendingUpdates (Gdk.Window window)
		{
//			Console.WriteLine ("Pending updates: " + redrawList.Count);
			renderedLines.Clear ();
			foreach (Gdk.Rectangle updateRect in redrawList.ToArray ()) {
				RenderMargins (this.buffer, updateRect);
				window.DrawDrawable (Style.BackgroundGC (StateType.Normal), buffer, updateRect.X, updateRect.Y, updateRect.X, updateRect.Y, updateRect.Width, updateRect.Height);
			}
			redrawList.Clear ();
		}
		
		#region TextEditorData functions
		public Mono.TextEditor.Highlighting.Style ColorStyle {
			get {
				return this.textEditorData.ColorStyle;
			}
		}
		
		public EditMode CurrentMode {
			get {
				return this.textEditorData.CurrentMode;
			}
			set {
				this.textEditorData.CurrentMode = value;
			}
		}
		
		public bool IsSomethingSelected {
			get {
				return this.textEditorData.IsSomethingSelected;
			}
		}
		
		public Selection MainSelection {
			get {
				return textEditorData.MainSelection;
			}
			set {
				textEditorData.MainSelection = value;
			}
		}
		
		public SelectionMode SelectionMode {
			get {
				return textEditorData.SelectionMode;
			}
			set {
				textEditorData.SelectionMode = value;
			}
		}

		public ISegment SelectionRange {
			get {
				return this.textEditorData.SelectionRange;
			}
			set {
				this.textEditorData.SelectionRange = value;
			}
		}
				
		public string SelectedText {
			get {
				return this.textEditorData.SelectedText;
			}
			set {
				this.textEditorData.SelectedText = value;
			}
		}
		
		public int SelectionAnchor {
			get {
				return this.textEditorData.SelectionAnchor;
			}
			set {
				this.textEditorData.SelectionAnchor = value;
			}
		}
		
		public IEnumerable<LineSegment> SelectedLines {
			get {
				return this.textEditorData.SelectedLines;
			}
		}
		
		public Adjustment HAdjustment {
			get {
				return this.textEditorData.HAdjustment;
			}
		}
		
		public Adjustment VAdjustment {
			get {
				return this.textEditorData.VAdjustment;
			}
		}
		
		public int Insert (int offset, string value)
		{
			return textEditorData.Insert (offset, value);
		}
		
		public void Remove (int offset, int count)
		{
			textEditorData.Remove (offset, count);
		}
		
		public int Replace (int offset, int count, string value)
		{
			return textEditorData.Replace (offset, count, value);
		}
		
		public void ClearSelection ()
		{
			this.textEditorData.ClearSelection ();
		}
		
		public void DeleteSelectedText ()
		{
			this.textEditorData.DeleteSelectedText ();
		}
		
		public void DeleteSelectedText (bool clearSelection)
		{
			this.textEditorData.DeleteSelectedText (clearSelection);
		}
		
		public void RunEditAction (Action<TextEditorData> action)
		{
			action (this.textEditorData);
		}
		
		public void SetSelection (int anchorOffset, int leadOffset)
		{
			this.textEditorData.SetSelection (anchorOffset, leadOffset);
		}
		
		public void SetSelection (DocumentLocation anchor, DocumentLocation lead)
		{
			this.textEditorData.SetSelection (anchor, lead);
		}
		
		public void ExtendSelectionTo (DocumentLocation location)
		{
			this.textEditorData.ExtendSelectionTo (location);
		}
		public void ExtendSelectionTo (int offset)
		{
			this.textEditorData.ExtendSelectionTo (offset);
		}
		public void SetSelectLines (int from, int to)
		{
			this.textEditorData.SetSelectLines (from, to);
		}
		
		public void InsertAtCaret (string text)
		{
			textEditorData.InsertAtCaret (text);
		}
		
		public bool CanEdit (int line)
		{
			return textEditorData.CanEdit (line);
		}
		
		
		/// <summary>
		/// Use with care.
		/// </summary>
		/// <returns>
		/// A <see cref="TextEditorData"/>
		/// </returns>
		public TextEditorData GetTextEditorData ()
		{
			return this.textEditorData;
		}
		
		public event EventHandler SelectionChanged;
		protected virtual void OnSelectionChanged (EventArgs args)
		{
			CurrentMode.InternalSelectionChanged (this, textEditorData);
			if (SelectionChanged != null) 
				SelectionChanged (this, args);
		}
		#endregion
		
		#region Search & Replace
		
		bool highlightSearchPattern = false;
		
		public string SearchPattern {
			get {
				return this.textEditorData.SearchRequest.SearchPattern;
			}
			set {
				if (this.textEditorData.SearchRequest.SearchPattern != value) {
					this.textEditorData.SearchRequest.SearchPattern = value;
					this.textViewMargin.ClearSearchMaker ();
					this.textViewMargin.DisposeLayoutDict ();
					this.QueueDraw ();
				}
			}
		}
		
		public ISearchEngine SearchEngine {
			get {
				return this.textEditorData.SearchEngine;
			}
			set {
				Debug.Assert (value != null);
				this.textEditorData.SearchEngine = value;
				this.QueueDraw ();
			}
		}
		
		public event EventHandler HighlightSearchPatternChanged;
		public bool HighlightSearchPattern {
			get {
				return highlightSearchPattern;
			}
			set {
				if (highlightSearchPattern != value) {
					this.highlightSearchPattern = value;
					if (HighlightSearchPatternChanged != null)
						HighlightSearchPatternChanged (this, EventArgs.Empty);
					textViewMargin.DisposeLayoutDict ();
					this.QueueDraw ();
				}
			}
		}
		
		public bool IsCaseSensitive {
			get {
				return this.textEditorData.SearchRequest.CaseSensitive;
			}
			set {
				this.textEditorData.SearchRequest.CaseSensitive = value;
			}
		}
		
		public bool IsWholeWordOnly {
			get {
				return this.textEditorData.SearchRequest.WholeWordOnly;
			}
			
			set {
				this.textEditorData.SearchRequest.WholeWordOnly = value;
			}
		}
		
		public SearchResult SearchForward (int fromOffset)
		{
			return textEditorData.SearchForward (fromOffset);
		}
		
		public SearchResult SearchBackward (int fromOffset)
		{
			return textEditorData.SearchBackward (fromOffset);
		}
		
		class HighlightSearchResultAnimation : IAnimationDrawer, IDisposable
		{
			TextEditor editor;
			SearchResult result;
			Gdk.Pixbuf textImage = null;
			
			public double Percent { get; set; }
			
			public HighlightSearchResultAnimation (TextEditor editor, SearchResult result)
			{
				this.editor = editor;
				this.result = result;
			}
			
			public void Draw (Drawable drawable)
			{
				LineSegment line = editor.Document.GetLineByOffset (result.Offset);
				int lineNr = editor.Document.OffsetToLineNumber (result.Offset);
				SyntaxMode mode = editor.Document.SyntaxMode != null && editor.Options.EnableSyntaxHighlighting ? editor.Document.SyntaxMode : SyntaxMode.Default;
				
				TextViewMargin.LayoutWrapper lineLayout = editor.textViewMargin.CreateLinePartLayout (mode, line, line.Offset, line.EditableLength, -1, -1);
				if (lineLayout == null)
					return;
				int l, x1, x2;
				int index = result.Offset - line.Offset - 1;
				if (index >= 0) {
					lineLayout.Layout.IndexToLineX (index, true, out l, out x1);
				} else {
					l = x1 = 0;
				}
				index = result.Offset - line.Offset - 1 + result.Length;
				if (index <= 0) 
					index = 1;
				lineLayout.Layout.IndexToLineX (index, true, out l, out x2);
				x1 /= (int)Pango.Scale.PangoScale;
				x2 /= (int)Pango.Scale.PangoScale;
				int y = editor.LineToVisualY (lineNr) - (int)editor.VAdjustment.Value;
				using (Cairo.Context cr = Gdk.CairoHelper.Create (drawable)) {
					cr.Rectangle (editor.TextViewMargin.XOffset, 0, editor.Allocation.Width - editor.TextViewMargin.XOffset, editor.Allocation.Height);
					cr.Clip ();
			
					
					int width = (int)(x2 - x1);
					int border = 2;
					int rx = (int)(editor.TextViewMargin.XOffset - editor.HAdjustment.Value + x1 - border);
					int ry = (int)(y) - border;
					int rw = width + border * 2;
					int rh = (int)(editor.LineHeight) + border * 2;
					
					int iw = width, ih = editor.LineHeight;
					if (textImage == null) {
						using (Gdk.Pixmap pixmap = new Gdk.Pixmap (drawable, iw, ih)) {
							using (var bgGc = new Gdk.GC(pixmap)) {
								bgGc.RgbFgColor = editor.ColorStyle.SearchTextMainBg;
								pixmap.DrawRectangle (bgGc, true, 0, 0, iw, ih);
								using (Pango.Layout layout = new Pango.Layout (editor.PangoContext)) {
									layout.FontDescription = editor.Options.Font;
									layout.SetMarkup (editor.Document.SyntaxMode.GetMarkup (editor.Document, editor.Options, editor.ColorStyle, result.Offset, result.Length, true));
									pixmap.DrawLayout (bgGc, 0, 0, layout);
								}
							}
							textImage = Pixbuf.FromDrawable (pixmap, Colormap.System, 0, 0, 0, 0, iw, ih);
						}
					}
					
					cr.Translate (rx + rw / 2, ry + rh / 2);
					/*cr.Save ();
					Cairo.Color color = Mono.TextEditor.Highlighting.Style.ToCairoColor (editor.ColorStyle.SearchTextBg);
					cr.Color = color;
					double scale2 = (1 + 1.1 * Percent / 6);
					cr.Scale (scale2, scale2 * 1.2);
					
					FoldingScreenbackgroundRenderer.DrawRoundRectangle (cr, true, true, -rw / 2, -rh / 2, (int)(System.Math.Min (10, width ) * editor.Options.Zoom), rw, rh);
					cr.Fill (); 
					cr.Restore ();*/
					
					double scale = 1 + Percent / 12;
					cr.Scale (scale, scale);
					double textx = -rw / 2;
					double texty = -rh / 2;
					cr.TransformPoint (ref textx, ref texty);
					
					double textr = +rw / 2;
					double textb = +rh / 2;
					cr.TransformPoint (ref textr, ref textb);
					
					cr.Color = new Cairo.Color (0, 0, 0, 0.3);
					//because the initial highlight is not rounded, so the rounding scales to make the transition smoother
					int rounding = (int)(-rw / 2 + 2 * editor.Options.Zoom * Percent);
					FoldingScreenbackgroundRenderer.DrawRoundRectangle (cr, true, true, rounding, (int)(-rh / 2 + 2 * editor.Options.Zoom), (int)(System.Math.Min (10, width ) * editor.Options.Zoom), rw, rh);
					cr.Fill (); 
					
					cr.Color = Mono.TextEditor.Highlighting.Style.ToCairoColor (editor.ColorStyle.SearchTextMainBg);
					FoldingScreenbackgroundRenderer.DrawRoundRectangle (cr, true, true, -rw / 2, -rh / 2, (int)(System.Math.Min (10, width ) * editor.Options.Zoom), rw, rh);
					cr.Fill ();
					
					int tx, ty, tw, th;
					tw = (int) System.Math.Ceiling (iw * scale);
					th = (int) System.Math.Ceiling (ih * scale);
					tx = rx - (int) System.Math.Ceiling ((double)(tw - iw) / 2) + border;
					ty = ry - (int) System.Math.Ceiling ((double)(th - ih) / 2) + border;
					try {
						using (var scaled = textImage.ScaleSimple (tw, th, InterpType.Bilinear)) {
							if (scaled != null) {
								using (var gc = new Gdk.GC (drawable)) {
									gc.ClipRectangle = new Rectangle (editor.TextViewMargin.XOffset, 0, editor.Allocation.Width - editor.TextViewMargin.XOffset, editor.Allocation.Height);
									scaled.RenderToDrawable (drawable, gc, 0, 0, tx, ty, tw, th, RgbDither.None, 0, 0);
								}
							}
						}
					} catch (Exception e) {
						Console.WriteLine ("got exception in search result animation:" + e);
					}
				}
				
				if (lineLayout.IsUncached) 
					lineLayout.Dispose ();
			}
			
			public void Dispose ()
			{
				if (this.textImage != null) {
					this.textImage.Dispose ();
					this.textImage = null;
				}
			}
			
		}
		
		class CaretPulseAnimation : IAnimationDrawer
		{
			TextEditor editor;
			
			public double Percent { get; set; }
			
			public CaretPulseAnimation (TextEditor editor)
			{
				this.editor = editor;
			}
			
			public void Draw (Drawable drawable)
			{
				int x = editor.TextViewMargin.caretX;
				int y = editor.TextViewMargin.caretY;
				if (editor.Caret.Mode != CaretMode.Block)
					x -= editor.TextViewMargin.charWidth / 2;
				using (Cairo.Context cr = Gdk.CairoHelper.Create (drawable)) {
					cr.Rectangle (editor.TextViewMargin.XOffset, 0, editor.Allocation.Width - editor.TextViewMargin.XOffset, editor.Allocation.Height);
					cr.Clip ();

					double extend = Percent * 5;
					int width = (int)(editor.TextViewMargin.charWidth + 2 * extend * editor.Options.Zoom / 2);
					FoldingScreenbackgroundRenderer.DrawRoundRectangle (cr, true, true, 
					                                                    (int)(x - extend * editor.Options.Zoom / 2), 
					                                                    (int)(y - extend * editor.Options.Zoom), 
					                                                    System.Math.Min (editor.TextViewMargin.charWidth / 2, width), 
					                                                    width,
					                                                    (int)(editor.LineHeight + 2 * extend * editor.Options.Zoom));
					Cairo.Color color = Mono.TextEditor.Highlighting.Style.ToCairoColor (editor.ColorStyle.Caret.Color);
					color.A = 0.8;
					cr.LineWidth = editor.Options.Zoom;
					cr.Color = color;
					cr.Stroke ();
				}
			}
		}
		
		public enum PulseKind {
			In, Out, Bounce
		}
		
		public class RegionPulseAnimation : IAnimationDrawer
		{
			TextEditor editor;
			
			public PulseKind Kind { get; set; }
			public double Percent { get; set; }
			
			Gdk.Rectangle region;
			
			public RegionPulseAnimation (TextEditor editor, Gdk.Point position, Gdk.Size size)
				: this (editor, new Gdk.Rectangle (position, size)) {}
			
			public RegionPulseAnimation (TextEditor editor, Gdk.Rectangle region)
			{
				if (region.X < 0 || region.Y < 0 || region.Width < 0 || region.Height < 0)
					throw new ArgumentException ("region is invalid");
				
				this.editor = editor;
				this.region = region;
			}
			
			public void Draw (Drawable drawable)
			{
				int x = region.X;
				int y = region.Y;
				int animationPosition = (int)(Percent * 100);
				
				using (Cairo.Context cr = Gdk.CairoHelper.Create (drawable)) {
					cr.Rectangle (editor.TextViewMargin.XOffset, 0, editor.Allocation.Width - editor.TextViewMargin.XOffset, editor.Allocation.Height);
					cr.Clip ();

					int width = (int)(region.Width + 2 * animationPosition * editor.Options.Zoom / 2);
					FoldingScreenbackgroundRenderer.DrawRoundRectangle (cr, true, true, 
					                                                    (int)(x - animationPosition * editor.Options.Zoom / 2), 
					                                                    (int)(y - animationPosition * editor.Options.Zoom), 
					                                                    System.Math.Min (editor.TextViewMargin.charWidth / 2, width), 
					                                                    width,
					                                                    (int)(region.Height + 2 * animationPosition * editor.Options.Zoom));
					Cairo.Color color = Mono.TextEditor.Highlighting.Style.ToCairoColor (editor.ColorStyle.Caret.Color);
					color.A = 0.8;
					cr.LineWidth = editor.Options.Zoom;
					cr.Color = color;
					cr.Stroke ();
				}
			}
		}
		
	/*	Gdk.Rectangle RangeToRectangle (int offset, int length)
		{
			DocumentLocation startLocation = Document.OffsetToLocation (offset);
			DocumentLocation endLocation = Document.OffsetToLocation (offset + length);
			
			if (startLocation.Column < 0 || startLocation.Line < 0 || endLocation.Column < 0 || endLocation.Line < 0)
				return Gdk.Rectangle.Zero;
			
			return RangeToRectangle (startLocation, endLocation);
		}*/
		
		Gdk.Rectangle RangeToRectangle (DocumentLocation start, DocumentLocation end)
		{
			if (start.Column < 0 || start.Line < 0 || end.Column < 0 || end.Line < 0)
				return Gdk.Rectangle.Zero;
			
			Gdk.Point startPt = this.textViewMargin.LocationToDisplayCoordinates (start);
			Gdk.Point endPt = this.textViewMargin.LocationToDisplayCoordinates (end);
			int width = endPt.X - startPt.X;
			
			if (startPt.Y != endPt.Y || startPt.X < 0 || startPt.Y < 0 || width < 0)
				return Gdk.Rectangle.Zero;
			
			return new Gdk.Rectangle (startPt.X, startPt.Y, width, this.textViewMargin.LineHeight);
		}
		
	/*	void AnimationTimer (object sender, EventArgs args)
		{
			if (animation != null) {
				animation.LifeTime--;
				if (animation.LifeTime < 0)
					animation = null;
				Application.Invoke (delegate {
					QueueDraw ();
				});
			} else {
				animationTimer.Stop ();
			}
		}*/
		
		/// <summary>
		/// Initiate a pulse at the specified document location
		/// </summary>
		/// <param name="pulseLocation">
		/// A <see cref="DocumentLocation"/>
		/// </param>
		public void PulseCharacter (DocumentLocation pulseStart)
		{
			if (pulseStart.Column < 0 || pulseStart.Line < 0)
				return;
			var rect = RangeToRectangle (pulseStart, new DocumentLocation (pulseStart.Line, pulseStart.Column + 1));
			if (rect.X < 0 || rect.Y < 0 || System.Math.Max (rect.Width, rect.Height) <= 0)
				return;
			StartAnimation (new RegionPulseAnimation (this, rect) {
				Kind = PulseKind.Bounce
			});
		}

		
		public SearchResult FindNext (bool setSelection)
		{
			SearchResult result = textEditorData.FindNext (setSelection);
			AnimateSearchResult (result);
			return result;
		}

		public void StartCaretPulseAnimation ()
		{
			StartAnimation (new TextEditor.CaretPulseAnimation (this));
		}
		
		Animation searchResultAnimation;
		public void AnimateSearchResult (SearchResult result)
		{
			TextViewMargin.MainSearchResult = result;
			if (result != null) {
				if (searchResultAnimation != null) 
					RemoveAnimation (searchResultAnimation);
				var anim = new TextEditor.HighlightSearchResultAnimation (this, result);
				searchResultAnimation = StartAnimation (anim, 120, Easing.QuadraticInOut);
			}
		}
	
		public SearchResult FindPrevious (bool setSelection)
		{
			SearchResult result = textEditorData.FindPrevious (setSelection);
			AnimateSearchResult (result);
			return result;
		}
		
		public bool Replace (string withPattern)
		{
			return textEditorData.SearchReplace (withPattern, true);
		}
		
		public int ReplaceAll (string withPattern)
		{
			return textEditorData.SearchReplaceAll (withPattern);
		}
		#endregion
	
		#region Tooltips

		public List<ITooltipProvider> TooltipProviders {
			get { return tooltipProviders; }
		}
		Timer tooltipTimer = null;
		void UpdateTooltip (Gdk.ModifierType modifierState)
		{
			if (tipWindow != null) {
				// Tip already being shown. Update it.
				ShowTooltip (modifierState);
			} else {
				DisposeTooltip ();
				tooltipTimer = new Timer (delegate { Application.Invoke (delegate { ShowTooltip (modifierState); }); }, null, TooltipTimer, System.Threading.Timeout.Infinite);

				// for some reason this leaks memory:
/*				// Tip already scheduled. Reset the timer.
				if (showTipScheduled)
					GLib.Source.Remove (tipTimeoutId);
				
				showTipScheduled = true;
				tipTimeoutId = GLib.Timeout.Add (TooltipTimer, delegate { return ShowTooltip (modifierState); });*/
			}
		}

		void DisposeTooltip ()
		{
			if (tooltipTimer != null)
				tooltipTimer.Dispose ();
		}
		
		bool ShowTooltip (Gdk.ModifierType modifierState)
		{
			return ShowTooltip (modifierState, 
			                    Document.LocationToOffset (VisualToDocumentLocation ((int)mx, (int)my)),
			                    (int)mx,
			                    (int)my);
		}
		
		bool ShowTooltip (Gdk.ModifierType modifierState, int offset, int xloc, int yloc)
		{
			CancelSheduledShow ();
			showTipScheduled = false;
			
			if (tipWindow != null && currentTooltipProvider.IsInteractive (this, tipWindow)) {
				int wx, ww, wh;
				tipWindow.GetSize (out ww, out wh);
				wx = tipX - ww/2;
				if (xloc >= wx && xloc < wx + ww && yloc >= tipY && yloc < tipY + 20 + wh)
					return false;
			}

			// Find a provider
			ITooltipProvider provider = null;
			object item = null;
			
			foreach (ITooltipProvider tp in tooltipProviders) {
				try {
					item = tp.GetItem (this, offset);
				} catch (Exception e) {
					System.Console.WriteLine ("Exception in tooltip provider " + tp + " GetItem:");
					System.Console.WriteLine (e);
				}
				if (item != null) {
					provider = tp;
					break;
				}
			}
			
			if (item != null) {
				// Tip already being shown for this item?
				if (tipWindow != null && tipItem != null && tipItem.Equals (item)) {
					CancelScheduledHide ();
					return false;
				}
				
				tipX = xloc;
				tipY = yloc;
				tipItem = item;


				Gtk.Window tw = provider.CreateTooltipWindow (this, offset, modifierState, item);
				if (tw == tipWindow)
					return false;
				HideTooltip ();
				if (tw == null)
					return false;
				
				tipShowTimeoutId = GLib.Timeout.Add (500, delegate {
					DoShowTooltip (provider, tw, tipX, tipY);
					tipShowTimeoutId = 0;
					return false;
				});
			} else
				HideTooltip ();
			
			return false;
		}
		uint tipShowTimeoutId = 0;
		void DoShowTooltip (ITooltipProvider provider, Gtk.Window liw, int xloc, int yloc)
		{
			CancelSheduledShow ();
			
			tipWindow = liw;
			currentTooltipProvider = provider;
			
			tipWindow.EnterNotifyEvent += delegate {
				CancelScheduledHide ();
			};
			
			int ox = 0, oy = 0;
			this.GdkWindow.GetOrigin (out ox, out oy);
			
			int screenW = Screen.Width;
			int w;
			double xalign;
			provider.GetRequiredPosition (this, liw, out w, out xalign);

			int x = xloc + ox + textViewMargin.XOffset - (int) ((double)w * xalign);
			if (x + w >= screenW)
				x = screenW - w;
			if (x < 0)
				x = 0;
			    
			tipWindow.Move (x, yloc + oy + 20);
			tipWindow.ShowAll ();
		}
		

		public void HideTooltip ()
		{
			CancelScheduledHide ();
			
			DisposeTooltip ();
			if (showTipScheduled) {
				GLib.Source.Remove (tipTimeoutId);
				showTipScheduled = false;
			}
			if (tipWindow != null) {
				tipWindow.Destroy ();
				tipWindow = null;
			}
		}
		
		void DelayedHideTooltip ()
		{
			CancelScheduledHide ();
			hideTipScheduled = true;
			RemoveTipTimer ();
			tipTimeoutId = GLib.Timeout.Add (300, delegate {
				HideTooltip ();
				return false;
			});
		}
		
		void CancelScheduledHide ()
		{
			CancelSheduledShow ();
			if (hideTipScheduled) {
				hideTipScheduled = false;
				GLib.Source.Remove (tipTimeoutId);
			}
		}
		
		void CancelSheduledShow ()
		{
			if (tipShowTimeoutId != 0) {
				GLib.Source.Remove (tipShowTimeoutId);
				tipShowTimeoutId = 0;
			}
		}
		
		void OnDocumentStateChanged (object s, EventArgs a)
		{
			HideTooltip ();
		}
		
		void OnTextSet (object sender, EventArgs e)
		{
			LineSegment longest = longestLine;
			foreach (LineSegment line in Document.Lines) {
				if (longest == null || line.EditableLength > longest.EditableLength)
					longest = line;
			}
			if (longest != longestLine) {
				int width = textViewMargin.ColumnToVisualX (longest, longest.EditableLength);
				if (width > this.longestLineWidth) {
					this.longestLineWidth = width;
					this.longestLine = longest;
				}
			}
		}
		#endregion

		
/*#region Container
		public override ContainerChild this [Widget w] {
			get {
				foreach (EditorContainerChild info in containerChildren.ToArray ()) {
					if (info.Child == w || (info.Child is AnimatedWidget && ((AnimatedWidget)info.Child).Widget == w))
						return info;
				}
				return null;
			}
		}
		
		public class EditorContainerChild : Container.ContainerChild
		{
			public int X { get; set; }
			public int Y { get; set; }
			public bool FixedPosition { get; set; }
			public EditorContainerChild (Container parent, Widget child) : base (parent, child)
			{
			}
		}
		
		public override GLib.GType ChildType ()
		{
			return Gtk.Widget.GType;
		}
		
		List<EditorContainerChild> containerChildren = new List<EditorContainerChild> ();
		
		public void AddTopLevelWidget (Gtk.Widget w, int x, int y)
		{
			w.Parent = this;
			EditorContainerChild info = new EditorContainerChild (this, w);
			info.X = x;
			info.Y = y;
			containerChildren.Add (info);
		}
		
		public void MoveTopLevelWidget (Gtk.Widget w, int x, int y)
		{
			foreach (EditorContainerChild info in containerChildren.ToArray ()) {
				if (info.Child == w || (info.Child is AnimatedWidget && ((AnimatedWidget)info.Child).Widget == w)) {
					info.X = x;
					info.Y = y;
					QueueResize ();
					break;
				}
			}
		}
		
		public void MoveTopLevelWidgetX (Gtk.Widget w, int x)
		{
			foreach (EditorContainerChild info in containerChildren.ToArray ()) {
				if (info.Child == w || (info.Child is AnimatedWidget && ((AnimatedWidget)info.Child).Widget == w)) {
					info.X = x;
					QueueResize ();
					break;
				}
			}
		}
		
		
		public void MoveToTop (Gtk.Widget w)
		{
			EditorContainerChild editorContainerChild = containerChildren.FirstOrDefault (c => c.Child == w);
			if (editorContainerChild == null)
				throw new Exception ("child " + w + " not found.");
			List<EditorContainerChild> newChilds = new List<EditorContainerChild> (containerChildren.Where (child => child != editorContainerChild));
			newChilds.Add (editorContainerChild);
			this.containerChildren = newChilds;
			w.GdkWindow.Raise ();
		}
		
		protected override void OnAdded (Widget widget)
		{
			AddTopLevelWidget (widget, 0, 0);
		}
		
		protected override void OnRemoved (Widget widget)
		{
			foreach (EditorContainerChild info in containerChildren.ToArray ()) {
				if (info.Child == widget) {
					widget.Unparent ();
					containerChildren.Remove (info);
					break;
				}
			}
		}
		
		protected override void OnSizeRequested (ref Gtk.Requisition requisition)
		{
			base.OnSizeRequested (ref requisition);
			
			// Ignore the size of top levels. They are supposed to fit the available space
			foreach (EditorContainerChild tchild in containerChildren.ToArray ())
				tchild.Child.SizeRequest ();
		}

		
		protected override void ForAll (bool include_internals, Gtk.Callback callback)
		{
			foreach (EditorContainerChild child in containerChildren.ToArray ()) {
				callback (child.Child);
			}
		}
#endregion*/
		
		#region Animation
		Stage<Animation> animationStage = new Stage<Animation> ();
		List<Animation> actors = new List<Animation> ();
		
		protected void InitAnimations ()
		{
			animationStage.ActorStep += OnAnimationActorStep;
			animationStage.Iteration += OnAnimationIteration;
		}
		
		Animation StartAnimation (IAnimationDrawer drawer)
		{
			return StartAnimation (drawer, 300);
		}
		
		Animation StartAnimation (IAnimationDrawer drawer, uint duration)
		{
			return StartAnimation (drawer, duration, Easing.Linear);
		}
		
		Animation StartAnimation (IAnimationDrawer drawer, uint duration, Easing easing)
		{
			if (!Options.EnableAnimations)
				return null;
			Animation animation = new Animation (drawer, duration, easing, Blocking.Upstage);
			animationStage.Add (animation, duration);
			actors.Add (animation);
			return animation;
		}
		
		bool OnAnimationActorStep (Actor<Animation> actor)
		{
			switch (actor.Target.AnimationState) {
			case AnimationState.Coming:
				actor.Target.Drawer.Percent = actor.Percent;
				if (actor.Expired) {
					actor.Target.AnimationState = AnimationState.Going;
					actor.Reset ();
					return true;
				}
				break;
			case AnimationState.Going:
				if (actor.Expired) {
					RemoveAnimation (actor.Target);
					return false;
				}
				actor.Target.Drawer.Percent = 1.0 - actor.Percent;
				break;
			}
			return true;
		}
		
		void RemoveAnimation (Animation animation)
		{
			if (animation == null)
				return;
			actors.Remove (animation);
			if (animation is IDisposable)
				((IDisposable)animation).Dispose ();
		}
		
		void OnAnimationIteration (object sender, EventArgs args)
		{
			QueueDraw ();
		}
		#endregion
		
		internal void FireLinkEvent (string link, int button, ModifierType modifierState)
		{
			if (LinkRequest != null)
				LinkRequest (this, new LinkEventArgs (link, button, modifierState));
		}
		
		public event EventHandler<LinkEventArgs> LinkRequest;
		
		/// <summary>
		/// Inserts a margin at the specified list position
		/// </summary>
		public void InsertMargin (int index, Margin margin)
		{
			margins.Insert (index, margin);
			RedrawFromLine (0);
		}
		
		/// <summary>
		/// Checks whether the editor has a margin of a given type
		/// </summary>
		public bool HasMargin (Type marginType)
		{
			return margins.Exists((margin) => { return marginType.IsAssignableFrom (margin.GetType ()); });
		}
		
		/// <summary>
		/// Gets the first margin of a given type
		/// </summary>
		public Margin GetMargin (Type marginType)
		{
			return margins.Find((margin) => { return marginType.IsAssignableFrom (margin.GetType ()); });
		}
		bool requestResetCaretBlink = false;
		public void RequestResetCaretBlink ()
		{
			if (this.IsFocus)
				requestResetCaretBlink = true;
		}

		public int CalculateLineNumber (int yPos)
		{
			int logicalLine = Document.VisualToLogicalLine (yPos / LineHeight);
			LineSegment logicalLineSegment = Document.GetLine (logicalLine);
			
			foreach (LineSegment extendedTextMarkerLine in Document.LinesWithExtendingTextMarkers) {
				if (logicalLineSegment != null && extendedTextMarkerLine.Offset >= logicalLineSegment.Offset)
					continue;
				int curLineHeight = GetLineHeight (extendedTextMarkerLine) - LineHeight;
				if (curLineHeight != 0) {
					logicalLine -= curLineHeight / LineHeight;
					logicalLineSegment = Document.GetLine (logicalLine);
				}
			}
			
			return logicalLine;
		}
		
		public int LineToVisualY (int logicalLine)
		{
			int delta = 0;
			LineSegment logicalLineSegment = Document.GetLine (logicalLine);
			foreach (LineSegment extendedTextMarkerLine in Document.LinesWithExtendingTextMarkers) {
				if (extendedTextMarkerLine == null)
					continue;
				if (logicalLineSegment != null && extendedTextMarkerLine.Offset >= logicalLineSegment.Offset)
					continue;
				delta += GetLineHeight (extendedTextMarkerLine) - LineHeight;
			}
			
			int visualLine = Document.LogicalToVisualLine (logicalLine);
			
			return visualLine * LineHeight + delta;
		}
		
		public int GetLineHeight (LineSegment line)
		{
			if (line == null || line.MarkerCount == 0)
				return LineHeight;
			foreach (var marker in line.Markers) {
				IExtendingTextMarker extendingTextMarker = marker as IExtendingTextMarker;
				if (extendingTextMarker == null)
					continue;
				return extendingTextMarker.GetLineHeight (this);
			}
			return LineHeight;
		}
		
		public int GetLineHeight (int logicalLineNumber)
		{
			return GetLineHeight (Document.GetLine (logicalLineNumber));
		}
		
		void UpdateLinesOnTextMarkerHeightChange (object sender, LineEventArgs e)
		{
			if (!e.Line.Markers.Any (m => m is IExtendingTextMarker))
				return;
			int currentHeight = GetLineHeight (e.Line);
			int h;
			if (lineHeights.TryGetValue (e.Line.Offset, out h) && h != currentHeight) {
				textEditorData.Document.CommitLineToEndUpdate (textEditorData.Document.OffsetToLineNumber (e.Line.Offset));
			}
			lineHeights[e.Line.Offset] = currentHeight;
		}
		
	}
	
	public interface ITextEditorDataProvider
	{
		TextEditorData GetTextEditorData ();
	}
}