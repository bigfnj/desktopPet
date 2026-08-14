using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;

namespace DesktopPet
{
        /// <summary>
        /// Form2 is the main class (form) of the pet. <br />
        /// Frames are borrowed from the Xml-owned shared sprite store and a Timer moves the pet.<br />
        /// The animations of this form is loaded from an XML.<br />
        /// </summary>
    public partial class FormPet : Form
    {
            /// <summary>
            /// Current step in the animation-frames list.
            /// </summary>
            /// <remarks>
            /// Every animation has a defined quantity of steps. They are calculated from:<br />
            /// - Quantity of frames<br />
            /// - Repeat count and repeat from<br />
            /// If an animation has 10 different frames and frame 5 to 9 are repeated 8 times, the total of steps is 10 + 5 * 8 = 50 steps.<br />
            /// Once the last step was reached, the next animation will be started. If now animation is set, the SPAWN will be executed.<br />
            /// </remarks>
        int AnimationStep;
            /// <summary>
            /// Structure with all informations about the current animation.
            /// </summary>
        TAnimation CurrentAnimation;
            /// <summary>
            /// Handle to the current window. If this value is 0, the sheep is NOT walking on a window.
            /// </summary>
        IntPtr hwndWindow = (IntPtr)0;
            /// <summary>
            /// Handle to the full screen window. If this value is 0, there is no full screen window.
            /// </summary>
        IntPtr hwndFullscreenWindow = (IntPtr)0;
        NativeMethods.RECT currentWindowSize;

            /// <summary>Forces the next <see cref="Play"/> onto a specific display (relocation); -1 = none.</summary>
        int _forcedDisplayIndex = -1;
        DateTime _lastFullscreenScanUtc = DateTime.MinValue;
        DateTime _lastRelocateUtc = DateTime.MinValue;
        bool _fullscreenHidden = false;   // hidden because every monitor is blocked (no free screen)

            /// <summary>
            /// If sheep is walking to left  (default).
            /// </summary>
            /// <remarks>
            /// The original eSheep was a Japanese application. So it was normal to see something from right to left.<br />
            /// To leave the same characteristic, moveLeft is set to true. But it doesn't matter, because the sprite and movements gives the direction...<br />
            /// </remarks>
        bool IsMovingLeft = true;
            /// <summary>
            /// Animations class. The entire animation and its values are described here.
            /// </summary>
        readonly Animations Animations;
            /// <summary>The pet TYPE id this instance belongs to ("eSheep" for the built-in default, or a
            /// folder id) — set on the shared Animations when the type is staged. Exposed for the plugin
            /// host so a module's IPet handle can report its type (S6p2 / per-pet config).</summary>
        internal string PetTypeId { get { return Animations != null ? (Animations.PetTypeId ?? "") : ""; } }
            /// <summary>
            /// Xml class. Xml parser and functionality are stored here.
            /// </summary>
        readonly Xml Xml;
            /// <summary>
            /// If the pet is in dragging mode (user is holding the pet with the mouse)
            /// </summary>
        bool IsDragging = false;

            /// <summary>
            /// True while the user is actively handling the pet. The AI idle loop reads this
            /// to avoid interrupting an interaction (backlog 3.5). Read-only, additive.
            /// </summary>
        public bool IsBusy { get { return IsDragging; } }

        /// <summary>
        /// Title of the window the pet is currently standing on (its title bar), or "" when it is
        /// roaming the desktop / taskbar. Used by the AI layer for screen-zone awareness (backlog 5.6).
        /// </summary>
        public string WindowUnderPet
        {
            get
            {
                try
                {
                    if (hwndWindow == IntPtr.Zero) return "";
                    StringBuilder sb = new StringBuilder(256);
                    NativeMethods.GetWindowText(hwndWindow, sb, sb.Capacity);
                    return sb.ToString().Trim();
                }
                catch { return ""; }
            }
        }
            /// <summary>
            /// If the pet is leaving the screen
            /// </summary>
        bool IsLeaving = false;
            /// <summary>
            /// Offset Y - Sprite size is taken and not the single image. So, over the taskbar or over the windows, the pet could be 1-2 pixels over the border if you didn't drawn it on the bottom of the sprite frame.<br />
            /// With this offset, you can re-place the pet or you can give them an offset so that it is positioned over the window (for example if you want to show a girl sitting over the taskbar, you need this function)
            /// </summary>
        double OffsetY = 0.0;
            /// <summary>
            /// Current X position of the form. Because an offset can be used, this is the origin of the sprite (not like Form2.Left) before an offset was interpolated with the form position.
            /// </summary>
        double PositionX = 0.0;
            /// <summary>
            /// Current Y position of the form. Because an offset can be used, this is the origin of the sprite (not like Form2.Top) before an offset was interpolated with the form position.
            /// </summary>
        double PositionY = 0.0;

            /// <summary>
            /// If multi screens are available, the pet can be set on a defined screen
            /// </summary>
        int DisplayIndex = 0;

        private readonly List<FormPet> childs = new List<FormPet>(4);
        private const int MaximumChildDepth = 5;
        private const int MaximumActiveChildrenPerRoot = 32;
        private const int MaximumActiveChildrenProcess = 64;
        private readonly FormPet parentPet;
        private readonly int childDepth;
        private readonly ChildBudget childBudget;
        private readonly Point parentPosition;
        private readonly bool parentWasFlipped;
        private bool childOwnershipReleased;

        // Speech bubble — one per pet instance, lazy-created
        private FormSpeech _speech;

        /// <summary>
        /// Form constructor. This is never called. <br />
        /// Form2(Animations animations, Xml xml) -> Called when a new sheep is generated<br />
        /// Form2(Animations animations, Xml xml, Point parentPos, bool parentFlipped) -> Called when a Child is generated<br />
        /// </summary>
        public FormPet()
        {
            InitializeComponent();
        }

            /// <summary>
            /// Form constructor.  Called when a new sheep is generated. 
            /// </summary>
            /// <param name="animations">Animation class, with all values.</param>
            /// <param name="xml">Xml class, with xml functions</param>
        public FormPet(Animations animations, Xml xml)
        {
            Animations = animations;
            Xml = xml;
            childBudget = new ChildBudget();
            childDepth = 0;
            parentPosition = new Point(-1, -1);
            InitializeComponent();
            Visible = false;            // Is invisible at beginning (we don't know where this sprite should be positioned)
            Opacity = 0.0;
            for (var s = 0; s < Screen.AllScreens.Length; s++)
            {
                if (Screen.AllScreens[s].Primary)
                {
                    DisplayIndex = s;
                    break;
                }
            }
        }

            /// <summary>
            /// Form constructor. Called when a Child is generated. 
            /// </summary>
            /// <param name="animations">Animation class, with all values.</param>
            /// <param name="xml">Xml class, with xml functions</param>
            /// <param name="parentPos">Position of the parent - used to detect where the child should be positioned</param>
            /// <param name="parentFlipped">If parent is flipped. If true, the child image will also be flipped</param>
            /// <param name="parentDisplay">Display Index of the parent. Put the child on same screen</param>
        private FormPet(
            Animations animations,
            Xml xml,
            FormPet parent,
            Point parentPos,
            bool parentFlipped,
            int parentDisplay,
            int depth,
            ChildBudget budget)
        {
            Animations = animations;
            Xml = xml;
            parentPet = parent;
            parentPosition = parentPos;
            parentWasFlipped = parentFlipped;
            childDepth = depth;
            childBudget = budget;
            DisplayIndex = parentDisplay;
			IsMovingLeft = !parentFlipped;
            InitializeComponent();
            Visible = false;            // Is invisible at beginning (we don't know where this sprite should be positioned)
            Opacity = 0.0;
        }

            /// <summary>
            /// With this overridden function, it is possible to remove the application from the ALT-TAB list.
            /// This, because it is not nice to see 10 times the same sheep when you press ALT-TAB (with 10 sheeps walking on your screen).
            /// If this form is a child, remove the possibility to interact with this form.
            /// See: https://msdn.microsoft.com/en-us/library/windows/desktop/ff700543(v=vs.85).aspx
            /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;

                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW             <- remove from ALT-TAB list
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST                <- set on TopMost
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED                <- increase paint performance
                //cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT            <- Do not draw window -> unclickable
                //cp.Style |= 0x80000000; // WS_POPUP

                if (Name.IndexOf("child") == 0)
                {
                    cp.ExStyle |= 0x08000000;   //WS_EX_NOACTIVATE  <- prevent focus when created
                }
                return cp;
            }
        }

        /// <summary>
        /// With this overridden function, it is possible to prevent the form to get the focus once created.
        /// </summary>
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>
        /// Once the form was created, this is the next function to call.
        /// It will set the size of the pet. Form will still be invisible because it has an opacity of 0.0.
        /// </summary>
        /// <param name="w">Single frame width</param>
        /// <param name="h">Single frame height</param>
        public void Show(int w, int h)
        {
            Width = w;
            Height = h;

            pictureBox1.Width = w;
            pictureBox1.Height = h;
            pictureBox1.Top = 0;
            pictureBox1.Left = 0;
            pictureBox1.Tag = 0;

			AnimationStep = 0;

            Show();
        }

        private Image GetSpriteFrame(int index)
        {
            if (Xml == null)
                throw new InvalidOperationException("Pet sprite frames are unavailable.");
            return Xml.GetSpriteFrame(index, !IsMovingLeft);
        }

        private void FlipOrientation()
        {
            IsMovingLeft = !IsMovingLeft;
        }

        internal Image SpriteFrameForDiagnostics(int index)
        {
            return GetSpriteFrame(index);
        }

        internal bool IsMovingLeftForDiagnostics
        {
            get { return IsMovingLeft; }
        }

        internal void FlipOrientationForDiagnostics()
        {
            FlipOrientation();
        }

        internal FormPet CreateUnshownChildForDiagnostics()
        {
            FormPet child = new FormPet(
                Animations,
                Xml,
                this,
                Point.Empty,
                !IsMovingLeft,
                DisplayIndex,
                childDepth + 1,
                null);
            child.Name = "child-diagnostic";
            return child;
        }

        private int ValidDisplayIndex
        {
            get
            {
                int count = Screen.AllScreens.Length;
                if (count <= 0) return 0;
                if (DisplayIndex < 0 || DisplayIndex >= count) DisplayIndex = 0;
                return DisplayIndex;
            }
        }
        private Rectangle ScreenBounds { get { return Screen.AllScreens[ValidDisplayIndex].Bounds; } }
        private Rectangle ScreenArea { get { return Screen.AllScreens[ValidDisplayIndex].WorkingArea; } }
        internal Rectangle CaptureScreenBounds
        {
            get
            {
                if (!IsDisposed && IsHandleCreated)
                    return Screen.FromRectangle(Bounds).Bounds;
                return ScreenBounds;
            }
        }

        /// <summary>
        /// Once the form was created, resized and all images was set, this is the next function to call.<br />
        /// It will initialize all variables and start the first animation (SPAWN).
        /// </summary>
        /// <param name="first">If it is playing a spawn for the first time. Does not have any functionality for the moment.</param>
        public void Play(bool first, int forceSpawn = -1)
        {
            timer1.Enabled = false;                     // Stop the timer

			AnimationStep = 0;                         // First step
            hwndWindow = (IntPtr)0;                     // It is not over a window

            // Multiscreen
            if(Program.MyData.GetMultiscreen())
            {
                int oldDisplayIndex = DisplayIndex;
                // A pending relocation (fullscreen game on the pet's monitor) forces the target screen;
                // otherwise the spawn picks a random screen as before.
                if (_forcedDisplayIndex >= 0 && _forcedDisplayIndex < Screen.AllScreens.Length)
                    DisplayIndex = _forcedDisplayIndex;
                else
                    DisplayIndex = new Random().Next(0, Screen.AllScreens.Length);
                if(oldDisplayIndex != DisplayIndex) // display changed, all computed values could be wrong
                {

                }
            }
            _forcedDisplayIndex = -1;   // consume the relocation request (also honored above when set)

            TSpawn spawn;
            if (forceSpawn < 0) spawn = Animations.GetRandomSpawn(); // Get a random SPAWN, to setting the form properties
            else
            {
                var k = Animations.SheepSpawn.Keys.ToList();
                spawn = Animations.SheepSpawn[k[forceSpawn]];
            }
            int spawnX = spawn.Start.X.GetValue(DisplayIndex);
            int spawnY = spawn.Start.Y.GetValue(DisplayIndex);
            spawnX = IsMovingLeft
                ? AnimationRuntimeLimits.ClampLocalPosition(spawnX, ScreenBounds.Width)
                : AnimationRuntimeLimits.MirrorLocalX(
                    spawnX,
                    ScreenBounds.Width,
                    pictureBox1.Width);
            spawnY = AnimationRuntimeLimits.ClampLocalPosition(
                spawnY,
                ScreenBounds.Height);
            Point spawnPosition = DesktopGeometry.MonitorLocalToVirtual(
                new Point(spawnX, spawnY), ScreenBounds);
            Left = spawnPosition.X;
            Top = spawnPosition.Y;
            pictureBox1.Left = 0;
            pictureBox1.Top = 0;
            Width = pictureBox1.Width;
            Height = pictureBox1.Height;
			PositionX = Left;
			PositionY = Top;
			OffsetY = 0.0;
            IsLeaving = false;
            SetNewAnimation(spawn.Next);                // Set next animation
            Visible = true;                             // Now we can show the form
            Opacity = 0.0;                              // do not show first frame (as it is undefined)
            timer1.Enabled = true;                      // Enable the timer (interval is well known now)
            TopMost = true;     // new in 1.2.6
        }

			/// <summary>
			/// If this form is a child, this function is called instead of Play().
			/// It will initialize all variables and start the first animation using CHILD, not SPAWN.
			/// </summary>
			/// <param name="aniID">Animation playing by the parent (child will synchronize to this animation).</param>
			/// <param name="child">The child to play (more than 1 childs can be played at the same time).</param>
		public void PlayChild(int aniID, TChild child)
        {
            timer1.Enabled = false;                     // Stop the timer

			AnimationStep = 0;                          // First step
            hwndWindow = (IntPtr)0;                     // It is not over a window

            using (CreateExpressionContext())
            {
                int childX = child.Position.X.GetValue(DisplayIndex);
                int childY = child.Position.Y.GetValue(DisplayIndex);
                childX = IsMovingLeft
                    ? AnimationRuntimeLimits.ClampLocalPosition(childX, ScreenBounds.Width)
                    : AnimationRuntimeLimits.MirrorLocalX(
                        childX,
                        ScreenBounds.Width,
                        pictureBox1.Width);
                childY = AnimationRuntimeLimits.ClampLocalPosition(
                    childY,
                    ScreenBounds.Height);
                Point childPosition = DesktopGeometry.MonitorLocalToVirtual(
                    new Point(childX, childY), ScreenBounds);
                Left = childPosition.X;
                Top = childPosition.Y;
			    PositionX = Left;
			    PositionY = Top;
			    OffsetY = 0.0;
                Visible = true;                         // Now we can show this child
                Opacity = 1.0;
                IsLeaving = false;
                pictureBox1.Cursor = Cursors.Default;

                SetNewAnimationCore(child.Next);        // Set next animation to play
            }

            timer1.Enabled = true;                      // Enable timer (interval is known, now)
        }

            /// <summary>
            /// If application is closed, all forms have still 1 second to show something (change animation).
            /// </summary>
            /// <remarks>
            /// Kill, Sync, Drag and Fall are "Key-names" in the XML file. If you use one of them, this program will automatically run the animation linked to this names.
            /// </remarks>
        public void Kill()
        {
            CloseChildren();
            if (Animations.AnimationKill > 1)
            {
                SetNewAnimation(Animations.AnimationKill);
            }
            else
            {
                Close();
                Dispose();
            }
        }

            /// <summary>
            /// If user press the CANCEL button in the about box, all pets are synchronized executing the SYNC-animation.
            /// </summary>
            /// <remarks>
            /// Kill, Sync, Drag and Fall are "Key-names" in the XML file. If you use one of them, this program will automatically run the animation linked to this names.
            /// </remarks>
        public void Sync()
        {
            if (Animations.AnimationSync > 1)
                SetNewAnimation(Animations.AnimationSync);
        }

            /// <summary>
            /// Timer tick. The entire animation is droved through this timer. The interval is set in the XML animation file.
            /// </summary>
            /// <remarks>
            /// On each tick, the next step is called. If it fails an error message will be show and the animation will stop.
            /// </remarks>
        private void Timer1_Tick(object sender, EventArgs e)
        {
            timer1.Enabled = false;
            if (AnimationStep < 0) AnimationStep = 0;
            try
            {
                // Poll independently of motion direction. Stationary, upward-only, dragged, and
                // window-attached pets must all yield to a fullscreen foreground window.
                CheckFullScreen();
                NextStep();
                if (IsDisposed)
                {
                    timer1.Enabled = false;
                }
                else
                {
					AnimationStep++;
                    UpdateSpeechFollow();   // keep any active bubble anchored over the pet's mouth
                    timer1.Enabled = true;
                }
            }
            catch(Exception ex) // if form is closed timer could continue to tick (why?)
            {
                if(MessageBox.Show("Fatal Error: " + ex.Message + "\n----------\nPress Cancel for more info", "App error", MessageBoxButtons.OKCancel, MessageBoxIcon.Error) == DialogResult.Cancel)
                {
                    string seqIndex = "";
                    foreach (var item in CurrentAnimation.Sequence.Frames) seqIndex += item + ",";

                        MessageBox.Show(
                        "Current Animation ID: " + CurrentAnimation.ID + "\n" +
                        "Current Animation Name: " + CurrentAnimation.Name + "\n" +
                        "Current Animation Sequence: " + seqIndex + "\n"
                        );

                }
            }
        }

            /// <summary>
            /// After an animation is over and after a new animation was selected, this function will play the selected animation.
            /// </summary>
            /// <param name="id">Animation ID to play.</param>
        private void SetNewAnimation(int id)
        {
            using (CreateExpressionContext())
                SetNewAnimationCore(id);
        }

        private void SetNewAnimationCore(int id)
        {
            if (CurrentAnimation.ID == Animations.AnimationKill) return;
            if (id < 0)  // no animation found, spawn!
            {
                Play(false);
            }
            else
            {
				AnimationStep = -1;
                CurrentAnimation = Animations.GetAnimation(id);
                CurrentAnimation.UpdateValues(DisplayIndex);

                // v.1.2.6: this will steal taskbar focus and the tray menu will disappear. So this should not be used too often.
                if (Program.MyData.GetStealTaskbarFocus() &&
                    hwndFullscreenWindow == IntPtr.Zero &&
                    CurrentAnimation.Start.OffsetY != 0 &&
                    CurrentAnimation.Start.X.Value != 0)
                {
                    TopMost = true; // bring to top again on each new animation
                }

                // Check if animation ID has a child. If so, the child will be created.
                if (Animations.HasAnimationChild(id))
                {
                    PruneClosedChildren();
                    if (childDepth < MaximumChildDepth)
                    {
						foreach (TChild childInfo in Animations.GetAnimationChild(id))
						{
                            if (childBudget == null || !childBudget.TryAcquire())
                            {
                                StartUp.AddDebugInfo(
                                    StartUp.DEBUG_TYPE.warning,
                                    "active child-pet limit reached");
                                break;
                            }

                            Point parentVirtual = new Point(
                                AnimationRuntimeLimits.ClampFormCoordinate(PositionX),
                                AnimationRuntimeLimits.ClampFormCoordinate(PositionY + OffsetY));
                            Point parentLocal = DesktopGeometry.VirtualToMonitorLocal(
                                parentVirtual, ScreenBounds);
                            FormPet child = null;
                            try
                            {
							    child = new FormPet(
                                    Animations,
                                    Xml,
                                    this,
                                    parentLocal,
                                    !IsMovingLeft,
                                    DisplayIndex,
                                    childDepth + 1,
                                    childBudget);
                                child.Name = "child" + (childDepth + 1).ToString();
                                childs.Add(child);
							    child.Show(
                                    Math.Max(1, pictureBox1.Width),
                                    Math.Max(1, pictureBox1.Height));
							    child.PlayChild(id, childInfo);
                            }
                            catch
                            {
                                if (child != null)
                                {
                                    childs.Remove(child);
                                    child.childOwnershipReleased = true;
                                    try { child.Close(); } catch { }
                                    try { child.Dispose(); } catch { }
                                }
                                childBudget.Release();
                                throw;
                            }
                        }
                    }
                }

                timer1.Interval = AnimationRuntimeLimits.ClampInterval(
                    CurrentAnimation.Start.Interval.Value);
            }
        }

            /// <summary>
            /// The most important function. Each movement step is managed by this function:<br />
            /// Will calculate how much and where a pet should be positioned in the next step.<br />
            /// This function is called from <see cref="Timer1_Tick(object, EventArgs)"/>.
            /// </summary>
        private void NextStep()
        {
            Rectangle monitorBounds = ScreenBounds;
            Rectangle workArea = ScreenArea;
            double workRight =
                (long)workArea.X + Math.Max(0L, (long)workArea.Width);
            double workBottom =
                (long)workArea.Y + Math.Max(0L, (long)workArea.Height);

            // A leaving animation temporarily shrinks the borderless form to the visible
            // slice of its sprite. Physics and border detection must always work from the
            // full sprite dimensions on the following tick.
            if (IsLeaving)
            {
                Width = Math.Max(1, pictureBox1.Width);
                Height = Math.Max(1, pictureBox1.Height);
                pictureBox1.Left = 0;
                pictureBox1.Top = 0;
                Left = AnimationRuntimeLimits.ClampFormCoordinate(PositionX);
                Top = AnimationRuntimeLimits.ClampFormCoordinate(PositionY + OffsetY);
            }

            int totalSteps = Math.Max(1, CurrentAnimation.Sequence.TotalSteps);
            int lastStep = AnimationRuntimeLimits.LastStepIndex(totalSteps);
            int frameStep = Math.Max(0, Math.Min(AnimationStep, lastStep));
            int interpolationSteps =
                AnimationRuntimeLimits.InterpolationSteps(totalSteps);

                // If there is no repeat, we don't need to calculate the frame index.
            int sequenceFrameIndex = AnimationRuntimeLimits.SequenceFrameIndex(
                frameStep,
                CurrentAnimation.Sequence.Frames.Count,
                CurrentAnimation.Sequence.RepeatFrom);
            pictureBox1.Image =
                GetSpriteFrame(CurrentAnimation.Sequence.Frames[sequenceFrameIndex]);

                // Get interval, opacity and offset interpolated from START and END values.
            long interval = CurrentAnimation.Start.Interval.Value +
                ((long)CurrentAnimation.End.Interval.Value -
                 CurrentAnimation.Start.Interval.Value) * frameStep / interpolationSteps;
            timer1.Interval = AnimationRuntimeLimits.ClampInterval(
                interval > int.MaxValue ? int.MaxValue :
                interval < int.MinValue ? int.MinValue : (int)interval);
            Opacity = Math.Max(
                0.0,
                Math.Min(
                    1.0,
                    CurrentAnimation.Start.Opacity +
                    (CurrentAnimation.End.Opacity - CurrentAnimation.Start.Opacity) *
                    frameStep / interpolationSteps));
			OffsetY = CurrentAnimation.Start.OffsetY +
                (double)(CurrentAnimation.End.OffsetY - CurrentAnimation.Start.OffsetY) *
                frameStep / interpolationSteps;

                // If dragging is enabled, move the pet to the mouse position.
            if (IsDragging)
            {
				PositionX = Left = Cursor.Position.X - Width / 2;
				PositionY = Top = Cursor.Position.Y - 2;
                return;
            }
            
            double x = CurrentAnimation.Start.X.Value;
            double y = CurrentAnimation.Start.Y.Value;
            // if TotalSteps is more than 1, we have to interpolate START and END values)
            if (CurrentAnimation.Sequence.TotalSteps > 1)
            {
                x += ((CurrentAnimation.End.X.Value - CurrentAnimation.Start.X.Value) * (double)frameStep / interpolationSteps);
                y += ((CurrentAnimation.End.Y.Value - CurrentAnimation.Start.Y.Value) * (double)frameStep / interpolationSteps);
            }
                // If a new animation need to be started
            bool bNewAnimation = false;
                // If animation is leaving screen, cut the form so that it is not visibile on multiscreens
            bool bLeavingScreen = false;
                // If the pet is "flipped", mirror the movement
            if (!IsMovingLeft) x = -x;
            
            if(x < 0)   // moving left (detect left borders)
            {
                if (hwndWindow == (IntPtr)0)
                {
                    if (PositionX + x < workArea.X)    // left screen border!
                    {
                        int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.VERTICAL);
                        if (iBorderAnimation >= 0)
                        {
                            PositionX = workArea.X;
                            x = 0;
                            SetNewAnimation(iBorderAnimation);
                            bNewAnimation = true;
                        }
                        else
                        {
                            bLeavingScreen = true;
                        }
                    }
                }
                else
                {
                    if (NativeMethods.GetWindowRect(new HandleRef(this, hwndWindow), out NativeMethods.RECT rct))
                    {
                        if (PositionX + x < rct.Left)    // left window border!
                        {
                            int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW);
                            if (iBorderAnimation >= 0)
                            {
                                PositionX = rct.Left;
                                x = 0;
                                SetNewAnimation(iBorderAnimation);
                                bNewAnimation = true;
                            }
                            else
                            {
                                // not anymore on the window
                                hwndWindow = (IntPtr)0;
                            }
                        }
                    }
                }
            }
            else if (x > 0)   // moving right (detect right borders)
            {
                if (hwndWindow == (IntPtr)0)
                {
                    if (PositionX + x + Width > workRight)    // right screen border!
                    {
                        
                        int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.VERTICAL);
                        if (iBorderAnimation >= 0)
                        {
                            PositionX = workRight - Width;
                            x = 0;
                            SetNewAnimation(iBorderAnimation);
                            bNewAnimation = true;
                        }
                        else
                        {
                            bLeavingScreen = true;
                        }
                    }
                }
                else
                {
                    if (NativeMethods.GetWindowRect(new HandleRef(this, hwndWindow), out NativeMethods.RECT rct))
                    {
                        if (PositionX + x + Width > rct.Right)    // right window border!
                        {
                            int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW);
                            if (iBorderAnimation >= 0)
                            {
                                PositionX = rct.Right - Width;
                                x = 0;
                                SetNewAnimation(iBorderAnimation);
                                bNewAnimation = true;
                            }
                            else
                            {
                                // not anymore on the window
                                hwndWindow = (IntPtr)0;
                            }
                        }
                    }
                }
            }
            if(bNewAnimation || bLeavingScreen)
            {
                // don't check anymore for y movement
            }
            else if(y > 0)   // moving down (detect taskbar and windows)
            {
                double bottomY = workBottom;

                if (PositionY + y > bottomY - Height) // border detected!
                {
                    int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.TASKBAR);
                    if (iBorderAnimation >= 0)
                    {
                        PositionY = bottomY - Height;
                        OffsetY = 0;
                        y = 0;
                        SetNewAnimation(iBorderAnimation);
                        bNewAnimation = true;
                    }
                    else
                    {
                        bLeavingScreen = true;
                    }
                }
                else
                {
                    WindowTopHit windowHit = FallDetect(y);
                    if (windowHit.Found)
                    {
                        int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW);
                        if (iBorderAnimation >= 0)
                        {
                            PositionY = windowHit.Top - Height;
                            OffsetY = 0;
                            y = 0;
                            SetNewAnimation(iBorderAnimation);
                            bNewAnimation = true;
                            if (CurrentAnimation.Start.Y.Value != 0)
                            {
                                hwndWindow = (IntPtr)0;
                            }
                        }
                    }
                }
            }
            else if(y < 0)  // moving up, detect upper screen border
            {
                if (PositionY + y < workArea.Y) // border detected!
                {
                    int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.HORIZONTAL);
                    if (iBorderAnimation >= 0)
                    {
                        PositionY = workArea.Y;
                        y = 0;
                        SetNewAnimation(iBorderAnimation);
                        bNewAnimation = true;
                    }
                    else
                    {
                        bLeavingScreen = true;
                    }
                }
            }

            if (AnimationStep >= lastStep) // the final declared frame was rendered; animation is over
            {
                int iNextAni;
                if(CurrentAnimation.Sequence.Action == "flip")
                {
                    // Select the shared, lazily materialized mirrored view. Never mutate the
                    // Xml-owned originals because every root and child borrows the same frames.
                    FlipOrientation();
                }
                if(hwndWindow != (IntPtr)0)
                {
                    iNextAni = Animations.SetNextSequenceAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW);
                }
                else
                {
                    // If the logical full sprite is outside the monitor, spawn it again.
                    // The physical form can be a one-pixel clipped anchor and must not be
                    // used for this decision.
                    double candidateX = AnimationRuntimeLimits.ClampVirtualPosition(
                        PositionX + x,
                        workArea.X,
                        workArea.Width);
                    double candidateY = AnimationRuntimeLimits.ClampVirtualPosition(
                        PositionY + y,
                        workArea.Y,
                        workArea.Height);
                    double candidateTop = AnimationRuntimeLimits.ClampVirtualPosition(
                        candidateY + OffsetY,
                        workArea.Y,
                        workArea.Height);
                    if (AnimationRuntimeLimits.IsSpriteFullyOutside(
                        candidateX,
                        candidateTop,
                        pictureBox1.Width,
                        pictureBox1.Height,
                        monitorBounds.X,
                        monitorBounds.Y,
                        monitorBounds.Width,
                        monitorBounds.Height))
                    {
                        iNextAni = -1;
                    }
                    else
                    {
                        iNextAni = Animations.SetNextSequenceAnimation(
                            CurrentAnimation.ID, 
                            PositionY + pictureBox1.Height + y >= workBottom - 2
                                ? TNextAnimation.TOnly.TASKBAR
                                : TNextAnimation.TOnly.NONE
                        );
                    }
                }
                if(CurrentAnimation.ID == Animations.AnimationKill)
                {
                    if (timer1.Tag == null) timer1.Tag = 1.0;

                    double op = double.Parse(timer1.Tag.ToString());
                    timer1.Tag = op - 0.1;
                    Opacity = op;
                    if (op <= 0.1)
                    {
                        Close();
                    }
                }
                else if (iNextAni >= 0)
                {
                    SetNewAnimation(iNextAni);
                    bNewAnimation = true;
                }
                else
                {
                        // Child doesn't have a spawn, they will be closed once the animation is over.
                    if(Name.IndexOf("child")==0)
                    {
                        StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "removing child");
                        Close();
                    }
                    else
                    {
                        Play(false);
                        return;
                    }
                }
            }
                // If there is a Gravity-Next animation, check if gravity is present.
            else if(CurrentAnimation.Gravity)
            {
                if(hwndWindow == (IntPtr)0)
                {
                    if(PositionY + y < workBottom - Height)
                    {
                        if(PositionY + y + 3 >= workBottom - Height) // allow 3 pixels to move without fall
                        {
                            y = workBottom - PositionY - Height;
                        }
                        else
                        {
                            SetNewAnimation(Animations.SetNextGravityAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.NONE));
                            bNewAnimation = true;
                        }
                    }
                }
                else
                {
                    if (AnimationStep > 0 && CheckTopWindow(true))
                    {
                        if (CurrentAnimation.Start.X.Value != 0 && FollowWindow())
                        {
                            PositionX = Left;
                            PositionY = Top - OffsetY;
                            return;
                        }
                        else
                        {
                            hwndWindow = (IntPtr)0;
                            SetNewAnimation(Animations.SetNextGravityAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW));
                            bNewAnimation = true;
                        }
                    }
                }
            }

                // If a new animation was started, set the interval and the first animation frame image.
            if(bNewAnimation)
            {
                timer1.Interval = 1;    // execute immediately the first step of the next animation.
                //x = 0;                  // don't move the pet, if a new animation must be started
                //y = 0;                  //  if falling, set the pet to the new position
                pictureBox1.Image = GetSpriteFrame(CurrentAnimation.Sequence.Frames[0]);
            }

			// Set the new pet position (and offset) in the screen. Keep logical
            // coordinates bounded before any conversion to WinForms integer bounds.
			PositionX = AnimationRuntimeLimits.ClampVirtualPosition(
                PositionX + x,
                workArea.X,
                workArea.Width);
			PositionY = AnimationRuntimeLimits.ClampVirtualPosition(
                PositionY + y,
                workArea.Y,
                workArea.Height);
            double renderTop = AnimationRuntimeLimits.ClampVirtualPosition(
                PositionY + OffsetY,
                workArea.Y,
                workArea.Height);

            int fullWidth = Math.Max(1, pictureBox1.Width);
            int fullHeight = Math.Max(1, pictureBox1.Height);
            int leftCut = AnimationRuntimeLimits.ClipCut(
                workArea.X - PositionX,
                fullWidth);
            int rightCut = AnimationRuntimeLimits.ClipCut(
                PositionX + fullWidth - workRight,
                fullWidth);
            int topCut = AnimationRuntimeLimits.ClipCut(
                workArea.Y - renderTop,
                fullHeight);
            int bottomCut = AnimationRuntimeLimits.ClipCut(
                renderTop + fullHeight - workBottom,
                fullHeight);
            bool hasCut =
                leftCut > 0 || rightCut > 0 || topCut > 0 || bottomCut > 0;

            // Derive clipping from absolute geometry on every tick. This also covers an
            // off-screen spawn moving inward and a leaving animation whose velocity
            // becomes zero; prior clipped dimensions are never reused cumulatively.
            if (hasCut)
            {
                IsLeaving = true;
                bool fullyClipped = false;
                int visibleWidth = Math.Max(0, fullWidth - leftCut - rightCut);
                int visibleHeight = Math.Max(0, fullHeight - topCut - bottomCut);

                if (visibleWidth > 0)
                {
                    Width = visibleWidth;
                    Left = AnimationRuntimeLimits.ClampFormCoordinate(
                        PositionX + leftCut);
                    pictureBox1.Left = -leftCut;
                }
                else
                {
                    fullyClipped = true;
                    Width = 1;
                    if (PositionX + fullWidth <= workArea.X)
                    {
                        Left = AnimationRuntimeLimits.ClampFormCoordinate(
                            (long)workArea.X - 1L);
                        pictureBox1.Left = -fullWidth;
                    }
                    else if (PositionX >= workRight)
                    {
                        Left = AnimationRuntimeLimits.ClampFormCoordinate(workRight);
                        pictureBox1.Left = fullWidth;
                    }
                    else
                    {
                        Left = AnimationRuntimeLimits.ClampFormCoordinate(workArea.X);
                        pictureBox1.Left = -leftCut;
                    }
                }

                if (visibleHeight > 0)
                {
                    Height = visibleHeight;
                    Top = AnimationRuntimeLimits.ClampFormCoordinate(
                        renderTop + topCut);
                    pictureBox1.Top = -topCut;
                }
                else
                {
                    fullyClipped = true;
                    Height = 1;
                    if (renderTop + fullHeight <= workArea.Y)
                    {
                        Top = AnimationRuntimeLimits.ClampFormCoordinate(
                            (long)workArea.Y - 1L);
                        pictureBox1.Top = -fullHeight;
                    }
                    else if (renderTop >= workBottom)
                    {
                        Top = AnimationRuntimeLimits.ClampFormCoordinate(workBottom);
                        pictureBox1.Top = fullHeight;
                    }
                    else
                    {
                        Top = AnimationRuntimeLimits.ClampFormCoordinate(workArea.Y);
                        pictureBox1.Top = -topCut;
                    }
                }

                if (fullyClipped)
                {
                    AnimationStep += Math.Max(
                        1,
                        CurrentAnimation.Sequence.Frames.Count / 3);
                }
            }
            else
            {
                IsLeaving = false;
                pictureBox1.Top = 0;
                pictureBox1.Left = 0;
                Width = fullWidth;
                Height = fullHeight;
                Left = AnimationRuntimeLimits.ClampFormCoordinate(PositionX);
                Top = AnimationRuntimeLimits.ClampFormCoordinate(renderTop);
            }

            
        }

            /// <summary>
            /// Detect if pet is still falling or if taskbar/window was detected.
            /// </summary>
            /// <param name="y">Y moves in pixels for the next step (function will detect if window/taskbar is inside the movement).</param>
            /// <returns>An explicit hit plus the virtual-screen Y coordinate of the window top.</returns>
        private WindowTopHit FallDetect(double y)
        {
            Dictionary<IntPtr, string> windows = new Dictionary<IntPtr, string>();
            NativeMethods.TITLEBARINFO titleBarInfo = new NativeMethods.TITLEBARINFO();
            titleBarInfo.cbSize = Marshal.SizeOf(titleBarInfo);

                // Enumerate all windows on the desktop.
            NativeMethods.EnumWindows(delegate (IntPtr hWnd, int lParam)
            {
                if (hWnd == Handle) return true;    // form itself, don't parse

                    // Enumerate only visible windows
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    StringBuilder sTitle = new StringBuilder(128);
                    NativeMethods.GetWindowText(hWnd, sTitle, 128);

                    // Sheep windows doesn't have a title bar, but we want detect if another pet is present
                    if (sTitle.ToString() == "Sheep") { }
                    // If there is no title bar, continue enumerating other windows
                    else if (!NativeMethods.GetTitleBarInfo(hWnd, ref titleBarInfo)) return true;
                    // If title bar is not visible, continue enumerating other windows
                    else if ((titleBarInfo.rgstate[0] & 0x00008000) > 0) // invisible
                        return true;
                    
                        // If window has a title, add this window to list
                    if (sTitle.Length > 0)
                    {
                        windows[hWnd] = sTitle.ToString();
                    }
                }
                return true;
            }, (IntPtr)0);

                // For each valid window found:
            foreach (KeyValuePair<IntPtr, string> window in windows)
            {
                    // Get size and position of window
                if (NativeMethods.GetWindowRect(new HandleRef(this, window.Key), out NativeMethods.RECT rct))
                {
                        // If vertical position is in the falling range and pet is over window and window is at least 20 pixels under the screen border
                    if (DesktopGeometry.CrossesDescendingBoundary(
                            PositionY + Height,
                            y,
                            rct.Top) &&
						PositionX >= rct.Left - Width / 2 && PositionX + Width <= rct.Right + Width / 2 &&
						PositionY > 20 + ScreenArea.Y)
                    {
                            // Pet need to walk over THIS window!
                        hwndWindow = window.Key;
                        currentWindowSize = rct;
						StringBuilder sTitle = new StringBuilder(128);
						NativeMethods.GetWindowText(hwndWindow, sTitle, 128);

						// If window is not covered by other windows, set this as current window for the pet.
						if (!CheckTopWindow(false))
                        {
								// Only if the option is set (this is an invasive functionality)
							if (Program.MyData.GetWindowForeground())
							{
								NativeMethods.ShowWindow(window.Key, 5);        // show window again
								NativeMethods.SetForegroundWindow(window.Key);  // set focus to window
							}
                            return WindowTopHit.At(rct.Top);               // return the position for the pet
                        }
                        else
                        {
                            hwndWindow = (IntPtr)0;                         // window is covered by other windows, reset handle
                        }
                    }
                }
            }
            return WindowTopHit.None;     // no windows detected.
        }

        // Sentinel stored in hwndFullscreenWindow while the pet's monitor is blocked. The value is
        // only ever tested against IntPtr.Zero (re-topmost gate + speech suppression), never used as
        // a real window handle, so a marker is enough.
        private static readonly IntPtr FullscreenBlockedMarker = (IntPtr)1;

        /// <summary>
        /// If a fullscreen (borderless or exclusive) window covers the pet's monitor, move the pet to
        /// a free monitor -- or hide it when none is free -- so it never sits on top of a game. Unlike
        /// a plain foreground check this walks the z-order (see <see cref="FullscreenScan"/>), so a
        /// sheep that stole focus by being grabbed over a borderless game still detects the game.
        /// </summary>
        private void CheckFullScreen()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastFullscreenScanUtc).TotalMilliseconds < 300) return;  // decouple from frame rate
            _lastFullscreenScanUtc = now;

            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) return;

            bool[] blocked;
            try
            {
                HashSet<IntPtr> pets =
                    Program.Mainthread != null ? Program.Mainthread.SheepHandles() : null;
                blocked = FullscreenScan.BlockedMonitors(pets);
            }
            catch { return; }
            if (blocked == null || blocked.Length != screens.Length) return;

            int current = 0;
            string device = Screen.FromRectangle(Bounds).DeviceName;
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].DeviceName == device) { current = i; break; }

            if (!blocked[current])
            {
                // Monitor is clear again: undo any hide/suppression and resume normal top-most.
                if (_fullscreenHidden) { _fullscreenHidden = false; if (!Visible) Visible = true; }
                if (hwndFullscreenWindow != IntPtr.Zero)
                {
                    hwndFullscreenWindow = IntPtr.Zero;
                    if (!TopMost) TopMost = true;
                    if (_speech != null && !_speech.IsDisposed)
                        _speech.SetFullscreenSuppressed(false);
                }
                return;
            }

            // Pet's monitor is blocked. Stop covering the game right away, then relocate or hide.
            hwndFullscreenWindow = FullscreenBlockedMarker;
            if (TopMost) TopMost = false;
            if (_speech != null && !_speech.IsDisposed)
                _speech.SetFullscreenSuppressed(true);

            // Children follow their parent's animation and must not re-spawn themselves.
            bool isChild = Name != null && Name.IndexOf("child") == 0;

            var monitors = new List<Rectangle>(screens.Length);
            for (int i = 0; i < screens.Length; i++) monitors.Add(screens[i].Bounds);
            int target = isChild
                ? -1
                : DesktopGeometry.ChooseRelocationTarget(current, monitors, blocked);

            if (target >= 0)
            {
                if ((now - _lastRelocateUtc).TotalMilliseconds < 1200) return;   // no rapid bouncing
                _lastRelocateUtc = now;
                _fullscreenHidden = false;
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(new Action(delegate { RelocateToDisplay(target); }));
            }
            else if (!_fullscreenHidden)
            {
                // No free monitor (single screen, every screen blocked, or a child): hide instead.
                _fullscreenHidden = true;
                if (Visible) Visible = false;
            }
        }

        /// <summary>Move the pet to <paramref name="target"/> and re-spawn there (a natural fall-in).</summary>
        private void RelocateToDisplay(int target)
        {
            if (IsDisposed) return;
            if (target < 0 || target >= Screen.AllScreens.Length) return;
            _fullscreenHidden = false;
            hwndFullscreenWindow = IntPtr.Zero;     // the target monitor is free; allow top-most again
            DisplayIndex = target;
            _forcedDisplayIndex = target;           // keep Play() from re-randomising under multiscreen
            Play(false);
        }

        private bool FollowWindow()
        {
            if (hwndWindow != IntPtr.Zero)
            {
				// Get window size and position of the current pet
				NativeMethods.RECT rctO;
                if (!NativeMethods.GetWindowRect(
                        new HandleRef(this, hwndWindow),
                        out rctO) ||
                    rctO.Right <= rctO.Left ||
                    rctO.Bottom <= rctO.Top)
                    return false;

				// window disappeared! Maybe it was closed.
				if (rctO.Top == 0 && rctO.Bottom == 0)
                {
                    return false;
                }

                if (currentWindowSize.Top != rctO.Top || currentWindowSize.Left != rctO.Left || currentWindowSize.Right != rctO.Right)
                {
                    // same width as before
                    if (rctO.Right - rctO.Left == currentWindowSize.Right - currentWindowSize.Left)
                    {
                        Top -= (currentWindowSize.Top - rctO.Top);
                        Left -= (currentWindowSize.Left - rctO.Left);
                    }
                    else // new width
                    {
                        int scaledLeft;
                        if (!DesktopGeometry.TryScaleWindowRelativeX(
                                Left,
                                currentWindowSize.Left,
                                currentWindowSize.Right,
                                rctO.Left,
                                rctO.Right,
                                out scaledLeft))
                            return false;
                        Top -= (currentWindowSize.Top - rctO.Top);
                        Left = scaledLeft;
                    }
                    currentWindowSize = rctO;
                    return true;
                }
            }
            return false;
        }

            /// <summary>
            /// Check if current window handler is still valid (if another window cover the visual of this window, it must not be used as window)
            /// </summary>
            /// <param name="bCheck">Check if it is still valid. Set false if window is not proofed, true if pet is already walking on a window => check if window is still valid.</param>
            /// <returns>True if window is still valid and present. False if window is not anymore there.</returns>
            /// <seealso cref="NativeMethods.GetWindow(IntPtr, int)"/>
            /// <seealso cref="NativeMethods.GetTitleBarInfo(IntPtr, ref NativeMethods.TITLEBARINFO)"/>
        private bool CheckTopWindow(bool bCheck)
        {
                // Check only if we have a valid window handler
            if (hwndWindow != IntPtr.Zero)
            {
				// Get window size and position of the current pet
				NativeMethods.GetWindowRect(new HandleRef(this, hwndWindow), out NativeMethods.RECT rctO);

				// If pet was walking on a window, check if window is still in the same position
				if (bCheck)
                {
                    if(currentWindowSize.Top != rctO.Top || currentWindowSize.Left != rctO.Left || currentWindowSize.Right != rctO.Right)
                    {
                        return true;
                    }
                }

                    // Get more informations about the current window title bar
                NativeMethods.TITLEBARINFO titleBarInfo = new NativeMethods.TITLEBARINFO();
                titleBarInfo.cbSize = Marshal.SizeOf(titleBarInfo);

                //Debug.WriteLine("Window TREE");
                
                // Get the handle to the first window (from user visual, in Z-order)
                IntPtr hwnd2 = NativeMethods.GetTopWindow((IntPtr)0);
                    // Loop until there are windows over the current window (in Z-Order)
                while (hwnd2 != (IntPtr)0)
                {
						// All windows up to the current window was parsed, now window is overlapping the current window
					if (hwnd2 == hwndWindow)
					{
                        //Debug.WriteLine("--XX Parsed all windows");
						return false;
					}

                    if (NativeMethods.IsWindowVisible(hwnd2))
                    {
                        StringBuilder sTitle = new StringBuilder(128);
                        NativeMethods.GetWindowText(hwnd2, sTitle, 128);

                        //Debug.WriteLine("--> " + sTitle);

                        // If window has a title bar
                        if (sTitle.Length > 0 && NativeMethods.GetTitleBarInfo(hwnd2, ref titleBarInfo))
                        {
                            // If window has a title name and a valid size and is not fullscreen
                            if (NativeMethods.GetWindowRect(new HandleRef(this, hwnd2), out NativeMethods.RECT rct) &&
                                (titleBarInfo.rcTitleBar.Bottom >= 0 || sTitle.ToString() == "sheep"))
                            {
                                //Debug.WriteLine("   -->  Pos:" + rct.Top + "," + rct.Left + " - Size:" + (rct.Right - rct.Left).ToString() + "," + (rct.Bottom - rct.Top).ToString());
                                if (rct.Top < rctO.Top && rct.Bottom > rctO.Top)
                                {
                                    if (rct.Left < PositionX && rct.Right > PositionX + 40/* && iAnimationStep > 4*/)
                                    {
                                        //Debug.WriteLine("   --> Window found!");
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                        // Get the handle to the next window (to user visual, in Z-order)
                    hwnd2 = NativeMethods.GetWindow(hwnd2, 2);
                }
            }
            return false;
        }
         
            /// <summary>
            /// Picture box fills the form, so mouse events are managed by this object: mouse pressed = pick pet. 
            /// </summary>
            /// <param name="sender">The caller object.</param>
            /// <param name="e">Mouse event values.</param>
        private void PictureBox1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Name.IndexOf("child") < 0)
            {
                hwndWindow = (IntPtr)0;             // Remove window handles
                TopMost = false;
                TopMost = true;                     // Set again the topmost
				IsDragging = true;                   // Flag it as dragging pet
                SetNewAnimation(Animations.AnimationDrag);  // Set the dragging animation (if present)
            }
            else if (e.Button == MouseButtons.Right && !StartUp.IsDebugActive())
            {
                // Poking the sheep (right-click) -> a fortune. (Full poke-escalation lands next.) The pet's
                // TYPE picks which speaker answers (per-pet voice, S6p2), falling back to the global choice.
                if (Program.Mainthread != null) Program.Mainthread.OnPetPoked(PetTypeId);
            }
            else if(e.Button == MouseButtons.Right && StartUp.IsDebugActive())
            {
                ContextMenu cm = new ContextMenu();
                cm.MenuItems.Add("ID." + CurrentAnimation.ID + " - " + CurrentAnimation.Name).Enabled = false;
                cm.MenuItems.Add("-");
                MenuItem menuNext = cm.MenuItems.Add("Next");
                MenuItem menuBorder = cm.MenuItems.Add("Border");
                MenuItem menuGravity = cm.MenuItems.Add("Gravity");
                cm.MenuItems.Add("-");
                MenuItem menuSpawn = cm.MenuItems.Add("Spawns");

                List<TNextAnimation> list = Animations.GetNextAnimations(CurrentAnimation.ID, true, false, false);
                foreach (TNextAnimation ani in list)
                {
                    MenuItem menu = menuNext.MenuItems.Add("ID." + ani.ID + " - " + Animations.SheepAnimations[ani.ID].Name + "\t (Prob: " + ani.Probability + ") only:" + ani.only.ToString());
                    menu.Click += (ms, me) => { SetNewAnimation(ani.ID); };
                }
                if (list.Count == 0) menuNext.Enabled = false;

                list = Animations.GetNextAnimations(CurrentAnimation.ID, false, true, false);
                foreach (TNextAnimation ani in list)
                {
                    MenuItem menu = menuBorder.MenuItems.Add("ID." + ani.ID + " - " + Animations.SheepAnimations[ani.ID].Name + "\t (Prob: " + ani.Probability + ") only: " + ani.only.ToString());
                    menu.Click += (ms, me) => { SetNewAnimation(ani.ID); };
                }
                if (list.Count == 0) menuBorder.Enabled = false;

                list = Animations.GetNextAnimations(CurrentAnimation.ID, false, false, true);
                foreach (TNextAnimation ani in list)
                {
                    MenuItem menu = menuGravity.MenuItems.Add("ID." + ani.ID + " - " + Animations.SheepAnimations[ani.ID].Name + "\t (Prob: " + ani.Probability + ") only:" + ani.only.ToString());
                    menu.Click += (ms, me) =>{ SetNewAnimation(ani.ID); };
                }
                if (list.Count == 0) menuGravity.Enabled = false;

                List<TSpawn> listS = Animations.GetNextSpawns();
                foreach (TSpawn spa in listS)
                {
                    MenuItem menu = menuSpawn.MenuItems.Add("ID." + spa.Next + " - " + Animations.SheepAnimations[spa.Next].Name + "\t (Prob: " + spa.Probability + ")");
                    menu.Click += (ms, me) => 
                    {
                        //Top = ScreenBounds.Y + spa.Start.Y.GetValue(DisplayIndex);
                        //Left = ScreenBounds.X + spa.Start.X.GetValue(DisplayIndex);
                        //PositionX = Left;
                        //PositionY = Top;
                        //OffsetY = 0.0;
                        int spawnIndex = menu.Index;
                        Play(false, spawnIndex);
                    };
                }

                timer1.Enabled = false;

                cm.Collapse += (ms, me) =>
                {
                    timer1.Interval = 1;
                    timer1.Enabled = true;
                };

                pictureBox1.ContextMenu = cm;
                pictureBox1.ContextMenu.Show(pictureBox1, new Point(0,this.Top > 500 ? 0 : this.Height));
            }
        }

            /// <summary>
            /// Mouse released the pet.
            /// </summary>
            /// <param name="sender">Caller object.</param>
            /// <param name="e">Mouse event values.</param>
        private void PictureBox1_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Name.IndexOf("child") < 0)
            {
                SetNewAnimation(Animations.AnimationFall);
            }
            if(IsDragging)
            {
                // if it was dragged, check if the screen is different
                // if(Program.MyData.GetMultiscreen()) <-- If manually moved to another screen, set the new screen as default screen.
                {
                    Point petCenter = new Point(Left + Width / 2, Top + Height / 2);
                    for(var k=0;k<Screen.AllScreens.Length;k++)
                    {
                        Rectangle bounds = Screen.AllScreens[k].Bounds;
                        if (bounds.Contains(petCenter))
                        {
                            if (DisplayIndex != k)
                            {
                                DisplayIndex = k;
                                CurrentAnimation.UpdateValues(DisplayIndex);
                            }
                            break;
                        }
                    }
                }
            }
			IsDragging = false;
        }
        
            /// <summary>
            /// Mouse double click on pet. From old eSheep, a double click with the right mouse will kill the sheep.
            /// </summary>
            /// <param name="sender">Caller object.</param>
            /// <param name="e">Mouse event values.</param>
        private void pictureBox1_DoubleClick(object sender, EventArgs e)
        {
            MouseEventArgs me = (MouseEventArgs)e;
            if (me.Button == MouseButtons.Right)
            {
                if(!Program.Mainthread.KillSheep(this))
                {
                    Close();
                }
            }
        }

        /// <summary>
        /// Pet allows dropping other files. If you drop a XML animation file, the mouse icon will change.
        /// </summary>
        /// <param name="sender">Caller object.</param>
        /// <param name="e">Mouse event values.</param>
        private void Form2_DragEnter(object sender, DragEventArgs e)
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "dragging file...");
            string ignored;
            e.Effect = TryGetSupportedPetDrop(e.Data, out ignored)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

            /// <summary>
            /// Pet allows dropping other files. If a XML file was dropped, this one will be loaded.
            /// </summary>
            /// <param name="sender">Caller object.</param>
            /// <param name="e">Dragging event values.</param>
        private void Form2_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                string file;
                if (!TryGetSupportedPetDrop(e.Data, out file)) return;

                string candidate;
                candidate = ReadBoundedPetXml(file);

                if (!Program.Mainthread.LoadNewXMLFromString(candidate))
                    MessageBox.Show(
                        "The dropped pet was rejected. The current pet is unchanged.",
                        "Invalid pet",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not load the dropped pet: " + ex.Message,
                    "Invalid pet",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static string ReadBoundedPetXml(string file)
        {
            int maximumBytes = PetXmlValidator.MaximumXmlBytes;
            byte[] bytes = new byte[checked(maximumBytes + 1)];
            int total = 0;
            PetXmlValidator.RetainedLocalXmlFile retained;
            string pathError;
            if (!PetXmlValidator.TryOpenLocalXmlFile(
                    file,
                    out retained,
                    out pathError))
                throw new InvalidDataException(pathError);
            using (retained)
            using (var stream = retained.OpenRead(4096))
            {
                while (total < bytes.Length)
                {
                    int read = stream.Read(bytes, total, bytes.Length - total);
                    if (read == 0) break;
                    total += read;
                }
            }

            if (total > maximumBytes)
                throw new InvalidDataException("Pet XML exceeds the 4 MiB limit.");

            return DecodePetXml(bytes, total);
        }

        private static string DecodePetXml(byte[] bytes, int count)
        {
            Encoding encoding = new UTF8Encoding(false, true);
            int offset = 0;

            if (count >= 4 &&
                bytes[0] == 0x00 && bytes[1] == 0x00 &&
                bytes[2] == 0xfe && bytes[3] == 0xff)
            {
                encoding = new UTF32Encoding(true, true, true);
                offset = 4;
            }
            else if (count >= 4 &&
                     bytes[0] == 0xff && bytes[1] == 0xfe &&
                     bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                encoding = new UTF32Encoding(false, true, true);
                offset = 4;
            }
            else if (count >= 3 &&
                     bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            {
                offset = 3;
            }
            else if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            {
                encoding = new UnicodeEncoding(true, true, true);
                offset = 2;
            }
            else if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            {
                encoding = new UnicodeEncoding(false, true, true);
                offset = 2;
            }

            return encoding.GetString(bytes, offset, count - offset);
        }

        private void Form2_FormClosed(object sender, FormClosedEventArgs e)
        {
            timer1.Stop();
            CloseChildren();
            pictureBox1.Image = null;
            if (_speech != null) _speech.Dispose();
            _speech = null;
            ReleaseChildOwnership();
            if(!IsDisposed) Dispose();
        }

        private static bool TryGetSupportedPetDrop(
            IDataObject data,
            out string canonicalPath)
        {
            canonicalPath = null;
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            string[] files = data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length != 1) return false;
            string error;
            return PetXmlValidator.TryResolveLocalXmlFile(
                files[0],
                out canonicalPath,
                out error);
        }

        /// <summary>
        /// Display a speech bubble above this pet.
        /// Does nothing when speech bubbles are disabled in Options.
        /// </summary>
        public void Say(string text)
        {
            if (!Program.MyData.GetSpeechEnabled()) return;

            if (_speech == null || _speech.IsDisposed)
                _speech = new FormSpeech();
            _speech.SetFullscreenSuppressed(
                hwndFullscreenWindow != IntPtr.Zero);

            SpriteSpeechAnchor anchor = GetSpeechAnchor();
            _speech.ShowSpeech(
                text,
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.X),
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.Top),
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.Bottom),
                Program.MyData.GetSpeechDuration(), IsMovingLeft);
        }

        internal bool PaintSpeechForResourceChurn()
        {
            if (!Program.ResourceChurnSelfTestActive ||
                _speech == null ||
                _speech.IsDisposed ||
                _speech.Width <= 0 ||
                _speech.Height <= 0)
                return false;
            using (var rendered =
                new Bitmap(_speech.Width, _speech.Height))
            {
                _speech.DrawToBitmap(
                    rendered,
                    new Rectangle(Point.Empty, rendered.Size));
            }
            return true;
        }

        /// <summary>
        /// Re-anchor an active speech bubble over the pet's mouth. Called from the pet's tick so
        /// the bubble follows the pet while it walks or falls, instead of hanging in mid-air where
        /// it first spoke. No-op when there's no bubble showing.
        /// </summary>
        private void UpdateSpeechFollow()
        {
            if (_speech == null || _speech.IsDisposed || !_speech.IsShowing) return;

            SpriteSpeechAnchor anchor = GetSpeechAnchor();
            _speech.Reposition(
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.X),
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.Top),
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.Bottom),
                IsMovingLeft);
        }

        private SpriteSpeechAnchor GetSpeechAnchor()
        {
            return DesktopGeometry.GetSpriteSpeechAnchor(
                PositionX,
                PositionY + OffsetY,
                pictureBox1.Width,
                pictureBox1.Height,
                IsMovingLeft);
        }

        /// <summary>
        /// Additive AI hook (backlog 2.8 / 3.6): play the animation whose XML name matches
        /// <paramref name="name"/> (case-insensitive). Returns false and does nothing when the
        /// loaded pet defines no such animation, so callers can map an emotion to a prioritized
        /// list of candidate names and fall through gracefully on pets that lack them.
        /// Must be called on the UI thread (it drives the same private SetNewAnimation the engine
        /// and the debug menu use); it does not otherwise touch the physics engine.
        /// </summary>
        public bool TryPlayAnimation(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Animations == null || Animations.SheepAnimations == null)
                return false;

            foreach (KeyValuePair<int, TAnimation> kv in Animations.SheepAnimations)
            {
                if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    SetNewAnimation(kv.Key);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Flee via the pet's "bathtub" spawn (fly in from the screen edge and land in a tub) — the
        /// climax of the poke-escalation. Finds the spawn whose next animation is named "bath*" and
        /// re-runs the engine's public Play(forceSpawn) path. Returns false when the loaded pet has
        /// no such spawn, so the caller can fall back. UI thread.
        /// </summary>
        public bool EscapeToBath()
        {
            try
            {
                if (Animations == null || Animations.SheepSpawn == null || Animations.SheepAnimations == null)
                    return false;

                List<int> keys = Animations.SheepSpawn.Keys.ToList();
                for (int i = 0; i < keys.Count; i++)
                {
                    int nextId = Animations.SheepSpawn[keys[i]].Next;
                    if (Animations.SheepAnimations.ContainsKey(nextId)
                        && (Animations.SheepAnimations[nextId].Name ?? "").StartsWith("bath", StringComparison.OrdinalIgnoreCase))
                    {
                        Play(false, i);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private IDisposable CreateExpressionContext()
        {
            if (childDepth <= 0)
                return Xml.PushParentContext(new Point(-1, -1), false);

            Point canonicalParent = new Point(
                AnimationRuntimeLimits.CanonicalParentX(
                    parentPosition.X,
                    parentWasFlipped,
                    ScreenBounds.Width,
                    pictureBox1.Width),
                parentPosition.Y);
            return Xml.PushParentContext(
                canonicalParent,
                false);
        }

        private void PruneClosedChildren()
        {
            for (int i = childs.Count - 1; i >= 0; i--)
            {
                FormPet child = childs[i];
                if (child == null || child.IsDisposed)
                {
                    childs.RemoveAt(i);
                    if (child != null) child.ReleaseChildOwnership();
                }
            }
        }

        private void CloseChildren()
        {
            FormPet[] snapshot = childs.ToArray();
            childs.Clear();
            foreach (FormPet child in snapshot)
            {
                if (child == null) continue;
                if (!child.IsDisposed)
                {
                    try { child.Close(); } catch { }
                    try { child.Dispose(); } catch { }
                }
                child.ReleaseChildOwnership();
            }
        }

        private void ReleaseChildOwnership()
        {
            if (childOwnershipReleased || childDepth <= 0) return;
            childOwnershipReleased = true;
            if (parentPet != null) parentPet.childs.Remove(this);
            if (childBudget != null) childBudget.Release();
        }

        private sealed class ChildBudget
        {
            private static readonly object GlobalSync = new object();
            private static int globalActive;
            private int active;

            public bool TryAcquire()
            {
                lock (GlobalSync)
                {
                    if (active >= MaximumActiveChildrenPerRoot ||
                        globalActive >= MaximumActiveChildrenProcess)
                        return false;
                    active++;
                    globalActive++;
                    return true;
                }
            }

            public void Release()
            {
                lock (GlobalSync)
                {
                    if (active <= 0) return;
                    active--;
                    if (globalActive > 0) globalActive--;
                }
            }
        }

		private void PictureBox1_Click(object sender, EventArgs e)
		{

		}
	}

	/// <summary>
	/// Native methods for the windows detection functionality. User32.dll is used for this.
	/// </summary>
	internal static class NativeMethods
    {
            /// <summary>
            /// Get size of a window.
            /// </summary>
            /// <param name="hWnd">Handle to window.</param>
            /// <param name="lpRect">returns the size of the window.</param>
            /// <returns>True if successfully.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(HandleRef hWnd, out RECT lpRect);

            /// <summary>
            /// Get a list of all windows present on the desktop.
            /// </summary>
            /// <param name="enumFunc">Enumeration function.</param>
            /// <param name="lParam">User defined value.</param>
            /// <returns>True if successfully.</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

            /// <summary>
            /// If window is visible (is on the desktop).
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <returns>True if successfully.</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

            /// <summary>
            /// Get the text present in the window title bar.
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <param name="lpString">Array, where the title should be copied.</param>
            /// <param name="nMaxCount">Array size.</param>
            /// <returns>Length of the title on the title bar.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            /// <summary>
            /// Get the values of the title bar from the window.
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <param name="pti">Pointer to a valid structure. Will be filled with all information.</param>
            /// <returns>True if successfully.</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTitleBarInfo(IntPtr hWnd, ref TITLEBARINFO pti);

            /// <summary>
            /// Change window modality (show, normal, hidden, maximize, ...) of a window.
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <param name="nCmdShow">Command to change modality.</param>
            /// <returns>True if successfully</returns>
            /// <seealso cref="ShowWindow(IntPtr, int)"/>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            /// <summary>
            /// Set the focus to the window and bring it to foreground. Used once the pet is felt over it.
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <returns>True</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

            /// <summary>
            /// Get the next window on the desktop (next to the user in Z-order, child, first window, etc.)
            /// </summary>
            /// <param name="hWnd">Handle to the current window.</param>
            /// <param name="nCmdShow">Command of the next window to get, <see cref="GetWindow(IntPtr, int)"/></param>
            /// <returns>Pointer to the next window.</returns>
        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// Get the window on the top, if hWnd is NULL, the top in Z-order will be returned
        /// </summary>
        /// <param name="hWnd">Handle to the current window.</param>
        /// <returns>Pointer to the next window.</returns>
        [DllImport("user32.dll")]
        internal static extern IntPtr GetTopWindow(IntPtr hWnd);


        /// <summary>
        /// Structure with the information about the title bar of the window.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TITLEBARINFO
        {
                /// <summary>
                /// Size (in bytes) of the current structure.
                /// </summary>
            public int cbSize;
                /// <summary>
                /// Dimension of the title bar.
                /// </summary>
            public RECT rcTitleBar;
                /// <summary>
                /// 6 bytes containing the states of the title bar.
                /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public int[] rgstate;
        }

            /// <summary>
            /// Dimension structure (used for the windows size).
            /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
                /// <summary>
                /// x position of upper-left corner
                /// </summary>
            public int Left;
                /// <summary>
                /// y position of upper-left corner
                /// </summary>
            public int Top;
                /// <summary>
                /// x position of lower-right corner
                /// </summary>
            public int Right;
                /// <summary>
                /// y position of lower-right corner
                /// </summary>
            public int Bottom; 
        }

            /// <summary>
            /// Procedure used to find all windows on the desktop.
            /// </summary>
            /// <param name="hWnd">Handle of the current found window.</param>
            /// <param name="lParam">User defined parameter.</param>
            /// <returns>True if successfully found another window.</returns>
        [return: MarshalAs(UnmanagedType.Bool)]
        internal delegate bool EnumWindowsProc(IntPtr hWnd, int lParam);
    }
}
