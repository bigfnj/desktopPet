using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace DesktopPet
{
    /// <summary>
    /// In-process port of the former tests\pettyperegistry-selftest.ps1. The PowerShell harness
    /// LoadFrom-ed the shipped assembly under Windows PowerShell 5.1 (.NET Framework); on .NET 10 no
    /// PowerShell hosts a net10 assembly, so the registry-lifecycle assertions run here as the
    /// --pettyperegistry-selftest flag, using the app's own internal types directly.
    /// </summary>
    internal static class PetTypeRegistrySelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            void Check(string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); if (!cond) ok = false; }

            FieldInfo dispX = typeof(Xml).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo dispA = typeof(Animations).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance);
            bool Disposed(FieldInfo f, object o) { return (bool)f.GetValue(o); }

            try
            {
                var reg = new PetTypeRegistry();
                PetTypeRegistry.Entry tmp;

                var x1 = new Xml(1); var a1 = new Animations(x1);
                PetTypeRegistry.Entry e1 = reg.Add("pink_sheep", x1, a1);
                Check("Add starts at refcount 0", e1.RefCount == 0);
                Check("entry is registered", reg.TryGet("pink_sheep", out tmp));
                reg.Increment(e1); reg.Increment(e1);
                Check("two Increments -> refcount 2", e1.RefCount == 2);
                reg.Decrement(e1);
                Check("one Decrement -> still alive at 1", e1.RefCount == 1 && reg.TryGet("pink_sheep", out tmp));
                Check("pair NOT disposed while refcount > 0", !Disposed(dispX, x1));
                reg.Decrement(e1);
                Check("removed from registry at refcount 0", !reg.TryGet("pink_sheep", out tmp));
                Check("Xml+Animations disposed exactly at zero", Disposed(dispX, x1) && Disposed(dispA, a1));

                bool threw = false;
                try { reg.Decrement(e1); } catch { threw = true; }
                Check("double-Decrement past zero is safe", !threw);

                var x2 = new Xml(2); var a2 = new Animations(x2);
                PetTypeRegistry.Entry e2 = reg.Add("red_sheep", x2, a2);
                reg.DropIfUnused(e2);
                Check("DropIfUnused disposes an unspawned type", !reg.TryGet("red_sheep", out tmp) && Disposed(dispX, x2));

                var x3 = new Xml(1); var a3 = new Animations(x3);
                PetTypeRegistry.Entry e3 = reg.Add("blue_sheep", x3, a3);
                reg.Increment(e3); reg.DropIfUnused(e3);
                Check("DropIfUnused leaves an in-use type alone", reg.TryGet("blue_sheep", out tmp) && !Disposed(dispX, x3));

                reg.DisposeAll();
                Check("DisposeAll disposes remaining pairs", !reg.TryGet("blue_sheep", out tmp) && Disposed(dispX, x3));

                // --- re-staging an id that is already registered ---
                // Displacing an UNREFERENCED entry must free it: nothing else owns that pair, so skipping
                // the dispose leaks it outright.
                var reg2 = new PetTypeRegistry();
                var xOldFree = new Xml(1); var aOldFree = new Animations(xOldFree);
                reg2.Add("green_sheep", xOldFree, aOldFree);
                var xNewFree = new Xml(1); var aNewFree = new Animations(xNewFree);
                PetTypeRegistry.Entry replacedFree = reg2.Add("green_sheep", xNewFree, aNewFree);
                Check("re-staging disposes a displaced UNREFERENCED pair",
                    Disposed(dispX, xOldFree) && Disposed(dispA, aOldFree));
                Check("re-staging keeps the new pair alive and registered",
                    !Disposed(dispX, xNewFree) && reg2.TryGet("green_sheep", out tmp) && ReferenceEquals(tmp, replacedFree));

                // Displacing an entry that live pets still BORROW must not free it: FormPet never disposes
                // its Xml/Animations, so disposing here would pull the sprites out from under a live pet.
                var xOldBusy = new Xml(1); var aOldBusy = new Animations(xOldBusy);
                PetTypeRegistry.Entry busy = reg2.Add("orange_sheep", xOldBusy, aOldBusy);
                reg2.Increment(busy);
                var xNewBusy = new Xml(1); var aNewBusy = new Animations(xNewBusy);
                PetTypeRegistry.Entry fresh = reg2.Add("orange_sheep", xNewBusy, aNewBusy);
                Check("re-staging does NOT dispose a displaced pair a live pet still borrows",
                    !Disposed(dispX, xOldBusy) && !Disposed(dispA, aOldBusy));

                // The regression this guards: DisposeEntry used to remove by KEY, so the displaced entry
                // reaching zero evicted the NEW entry from the map. A live pet's type then vanished from the
                // registry and the next spawn staged a third duplicate copy of the same pet.
                reg2.Decrement(busy);
                Check("the displaced pair is freed when ITS last pet closes", Disposed(dispX, xOldBusy));
                Check("...without evicting the entry that now owns the id",
                    reg2.TryGet("orange_sheep", out tmp) && ReferenceEquals(tmp, fresh) && !Disposed(dispX, xNewBusy));
                reg2.Increment(fresh); reg2.Decrement(fresh);
                Check("the current entry still disposes normally at zero",
                    !reg2.TryGet("orange_sheep", out tmp) && Disposed(dispX, xNewBusy));
                reg2.DisposeAll();

                // --- the on-screen mix, the single choke point for persistence AND the tray ---
                var xMix = new Xml(1); var aMix = new Animations(xMix);
                var installed = new PetTypeRegistry.Entry { Id = "pearl", Xml = xMix, Animations = aMix };
                var preview = new PetTypeRegistry.Entry { Id = "preview:abc", IsTransient = true };
                System.Collections.Generic.List<PetCountEntry> mix = StartUp.DeriveOnScreenMix(
                    new[] { null, null, preview, installed });
                Check("mix counts active-type pets under \"\" in first-appearance order",
                    mix.Count == 2 && mix[0].Id == "" && mix[0].Count == 2);
                Check("mix counts an installed type under its id", mix[1].Id == "pearl" && mix[1].Count == 1);
                Check("a TRANSIENT (preview) pet never reaches the mix",
                    mix.TrueForAll(e => e.Id.IndexOf("preview", StringComparison.OrdinalIgnoreCase) < 0));
                Check("a screen holding only previews yields an EMPTY mix (nothing to persist)",
                    StartUp.DeriveOnScreenMix(new[] { preview }).Count == 0);
                Check("no pets yields an empty mix", StartUp.DeriveOnScreenMix(new PetTypeRegistry.Entry[0]).Count == 0);
                aMix.Dispose(); xMix.Dispose();

                if (ok) sb.AppendLine("PASS: PetTypeRegistry lifetime self-test.");
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-pettyperegistry-selftest.txt"), sb.ToString()); } catch { }
            return ok;
        }
    }

    /// <summary>
    /// In-process port of the reflection half of tests\runtime-hardening-selftest.ps1 (the animation/
    /// geometry/runtime-limit invariants). Runs as the --hardening-selftest flag. AnimationRuntimeLimits
    /// (public static math) is called directly; non-public members are exercised by reflection over this
    /// assembly, mirroring the original harness exactly. The source-text invariant checks remain in the
    /// PowerShell script (they read .cs files, which the shipped app cannot).
    /// </summary>
    internal static class RuntimeHardeningSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            void Check(string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); if (!cond) ok = false; }
            void CheckRejects(string name, Action action)
            {
                try { action(); Check(name, false); }
                catch (Exception ex)
                {
                    Exception inner = ex;
                    while (inner.InnerException != null) inner = inner.InnerException;
                    Check(name, inner is InvalidDataException);
                }
            }

            Assembly asm = typeof(RuntimeHardeningSelfTest).Assembly;
            const BindingFlags PubInstance = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags NpStatic = BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags NpInstance = BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                // ---- AnimationRuntimeLimits: direct calls (public static math) ----
                Check("no-repeat total steps", AnimationRuntimeLimits.CalculateTotalSteps(3, 1, 0) == 3);
                Check("repeat total steps", AnimationRuntimeLimits.CalculateTotalSteps(3, 1, 1) == 5);
                Check("repeat clamp", AnimationRuntimeLimits.CalculateTotalSteps(3, 1, int.MaxValue) == 2003);
                Check("total-step cap", AnimationRuntimeLimits.CalculateTotalSteps(16384, 0, 1000) == 1000000);
                Check("one-frame last step", AnimationRuntimeLimits.LastStepIndex(1) == 0);
                Check("multi-frame last step", AnimationRuntimeLimits.LastStepIndex(5) == 4);
                Check("one-frame interpolation divisor", AnimationRuntimeLimits.InterpolationSteps(1) == 1);
                Check("endpoint interpolation divisor", AnimationRuntimeLimits.InterpolationSteps(5) == 4);

                var frames = new int[5];
                for (int i = 0; i < 5; i++) frames[i] = AnimationRuntimeLimits.SequenceFrameIndex(i, 3, 1);
                Check("repeat-from frame order", string.Join(",", frames) == "0,1,2,1,2");

                Check("negative coordinate clamp", AnimationRuntimeLimits.ClampLocalPosition((long)int.MinValue, 1920) == -8192);
                Check("positive coordinate clamp", AnimationRuntimeLimits.ClampLocalPosition((long)int.MaxValue, 1920) == 10112);
                Check("normal mirror arithmetic", AnimationRuntimeLimits.MirrorLocalX(100, 1920, 64) == 1756);

                int rightParent = AnimationRuntimeLimits.MirrorLocalX(100, 1920, 64);
                int canonParent = AnimationRuntimeLimits.CanonicalParentX(rightParent, true, 1920, 64);
                Check("flipped parent is canonicalized before child expression evaluation", canonParent == 100);
                int leftChild = canonParent + 64 + 10;
                int rightChild = AnimationRuntimeLimits.MirrorLocalX(leftChild, 1920, 32);
                Check("child placement has left-right symmetry with one full-screen mirror", leftChild + rightChild == 1888);

                Check("overflow-safe mirror arithmetic", AnimationRuntimeLimits.MirrorLocalX(int.MinValue, 1920, 64) == 10112);
                Check("maximum monitor extent never wraps", AnimationRuntimeLimits.ClampLocalPosition((long)int.MaxValue + 1L, int.MaxValue) == int.MaxValue);
                Check("maximum-width mirror never wraps", AnimationRuntimeLimits.MirrorLocalX(-100, int.MaxValue, 0) == int.MaxValue);
                Check("positive infinite virtual coordinate clamps", AnimationRuntimeLimits.ClampVirtualPosition(double.PositiveInfinity, int.MaxValue, int.MaxValue) == (double)int.MaxValue);

                Check("first absolute clipping cut", AnimationRuntimeLimits.ClipCut(14.0, 64) == 14);
                Check("second absolute clipping cut", AnimationRuntimeLimits.ClipCut(24.0, 64) == 24);
                Check("first visible clipping extent", 64 - AnimationRuntimeLimits.ClipCut(14.0, 64) == 50);
                Check("second visible clipping extent is not cumulative", 64 - AnimationRuntimeLimits.ClipCut(24.0, 64) == 40);
                Check("large positive clipping jump clamps to full extent", AnimationRuntimeLimits.ClipCut(32768.0, 64) == 64);
                Check("negative clipping amount is ignored", AnimationRuntimeLimits.ClipCut(-32768.0, 64) == 0);
                Check("bottom clipping cut", AnimationRuntimeLimits.ClipCut(24.0, 64) == 24);
                Check("simultaneous horizontal cuts retain viewport slice", 40 - AnimationRuntimeLimits.ClipCut(10.0, 40) - AnimationRuntimeLimits.ClipCut(10.0, 40) == 20);
                Check("positive form coordinate saturation", AnimationRuntimeLimits.ClampFormCoordinate(double.PositiveInfinity) == int.MaxValue);
                Check("negative form coordinate saturation", AnimationRuntimeLimits.ClampFormCoordinate(double.NegativeInfinity) == int.MinValue);

                Check("exact full left cut is outside", AnimationRuntimeLimits.IsSpriteFullyOutside(-64.0, 100.0, 64, 64, 0, 0, 1920, 1080));
                Check("exact full right cut is outside", AnimationRuntimeLimits.IsSpriteFullyOutside(1920.0, 100.0, 64, 64, 0, 0, 1920, 1080));
                Check("exact full top cut is outside", AnimationRuntimeLimits.IsSpriteFullyOutside(100.0, -64.0, 64, 64, 0, 0, 1920, 1080));
                Check("exact full bottom cut is outside", AnimationRuntimeLimits.IsSpriteFullyOutside(100.0, 1080.0, 64, 64, 0, 0, 1920, 1080));
                Check("one-pixel inward slice remains visible", !AnimationRuntimeLimits.IsSpriteFullyOutside(-63.0, 100.0, 64, 64, 0, 0, 1920, 1080));
                Check("extreme monitor edge arithmetic does not wrap", AnimationRuntimeLimits.IsSpriteFullyOutside(4294967294.0, 4294967294.0, 64, 64, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));

                // ---- TValue evaluator ownership + scale isolation + weight + sprite budget (reflection) ----
                Type xmlT = asm.GetType("DesktopPet.Xml", true);
                Type tvalueT = asm.GetType("DesktopPet.TValue", true);
                Type animT = asm.GetType("DesktopPet.Animations", true);
                MethodInfo xmlDispose = xmlT.GetMethod("Dispose", PubInstance);
                object xmlOne = Activator.CreateInstance(xmlT, new object[] { 1 });
                object xmlTwo = Activator.CreateInstance(xmlT, new object[] { 2 });
                try
                {
                    MethodInfo compute = xmlT.GetMethod("GetXMLCompute", PubInstance);
                    object valueOne = compute.Invoke(xmlOne, new object[] { "scale", "ownership-one" });
                    object valueTwo = compute.Invoke(xmlTwo, new object[] { "scale", "ownership-two" });
                    FieldInfo evaluator = tvalueT.GetField("Evaluator", NpInstance);
                    Check("first TValue evaluator ownership", ReferenceEquals(xmlOne, evaluator.GetValue(valueOne)));
                    Check("second TValue evaluator ownership", ReferenceEquals(xmlTwo, evaluator.GetValue(valueTwo)));
                    MethodInfo getRaw = tvalueT.GetMethod("GetRawValue", PubInstance);
                    Check("first evaluator scale isolation", (int)getRaw.Invoke(valueOne, new object[] { -1 }) == 1);
                    Check("second evaluator scale isolation", (int)getRaw.Invoke(valueTwo, new object[] { -1 }) == 2);

                    object animations = Activator.CreateInstance(animT, new object[] { xmlOne });
                    try
                    {
                        MethodInfo nextWeight = animT.GetMethod("NextWeight", NpInstance);
                        long upper = (long)int.MaxValue + 1L;
                        bool inRange = true;
                        for (int i = 0; i < 2048; i++)
                        {
                            long sample = Convert.ToInt64(nextWeight.Invoke(animations, new object[] { upper }));
                            if (sample < 0 || sample >= upper) { inRange = false; break; }
                        }
                        Check("weight selection above int.MaxValue", inRange);
                    }
                    finally { animT.GetMethod("Dispose", PubInstance).Invoke(animations, new object[0]); }

                    MethodInfo validateBudget = xmlT.GetMethod("ValidateSpriteBudget", NpStatic);
                    validateBudget.Invoke(null, new object[] { 32, 32, 128, 128 });
                    Check("exact sprite pixel budget accepted", true);
                    CheckRejects("oversized generated-frame count rejected", () => validateBudget.Invoke(null, new object[] { 41, 25, 1, 1 }));
                    CheckRejects("oversized generated-pixel budget rejected", () => validateBudget.Invoke(null, new object[] { 32, 32, 256, 256 }));
                }
                finally
                {
                    xmlDispose.Invoke(xmlOne, new object[0]);
                    xmlDispose.Invoke(xmlTwo, new object[0]);
                }

                // ---- bundled pet decodes at 4x within budgets (reflection) ----
                Type resourcesT = asm.GetType("DesktopPet.Properties.Resources", true);
                PropertyInfo animProp = resourcesT.GetProperty("animations", NpStatic);
                string bundledXml = (string)animProp.GetValue(null, new object[0]);
                object scaledXml = Activator.CreateInstance(xmlT, new object[] { 4 });
                try
                {
                    MethodInfo tryRead = xmlT.GetMethod("TryReadXml", PubInstance);
                    Check("bundled pet decodes at requested 4x scale", (bool)tryRead.Invoke(scaledXml, new object[] { bundledXml, null }));
                    PropertyInfo spriteCountP = xmlT.GetProperty("SpriteCount", BindingFlags.Instance | BindingFlags.NonPublic);
                    int spriteCount = (int)spriteCountP.GetValue(scaledXml, new object[0]);
                    Check("bundled pet generated-frame budget", spriteCount <= 1024);
                    long sw = Convert.ToInt64(MemberValue(xmlT, scaledXml, "spriteWidth"));
                    long sh = Convert.ToInt64(MemberValue(xmlT, scaledXml, "spriteHeight"));
                    Check("bundled pet generated-pixel budget", ((long)spriteCount * sw * sh) <= (16L * 1024L * 1024L));
                }
                finally { xmlDispose.Invoke(scaledXml, new object[0]); }

                // ---- bundled pet honours a SUB-1 scale (the size slider going below 1x) ----
                object oneXxml = Activator.CreateInstance(xmlT, new object[] { 1.0 });
                object halfXml = Activator.CreateInstance(xmlT, new object[] { 0.5 });
                try
                {
                    MethodInfo tryRead2 = xmlT.GetMethod("TryReadXml", PubInstance);
                    tryRead2.Invoke(oneXxml, new object[] { bundledXml, null });
                    tryRead2.Invoke(halfXml, new object[] { bundledXml, null });
                    long w1 = Convert.ToInt64(MemberValue(xmlT, oneXxml, "spriteWidth"));
                    long wHalf = Convert.ToInt64(MemberValue(xmlT, halfXml, "spriteWidth"));
                    Check("sub-1 scale shrinks the frame below 1x", wHalf >= 1 && wHalf < w1);
                    Check("0.5x is about half the 1x width", System.Math.Abs(wHalf * 2 - w1) <= 2);
                    Check("sub-1 keeps the 'scale' expression integer >= 1",
                        (int)xmlT.GetProperty("ScaleFactor", PubInstance).GetValue(halfXml, null) == 1);
                }
                finally { xmlDispose.Invoke(oneXxml, new object[0]); xmlDispose.Invoke(halfXml, new object[0]); }

                // ---- speech bubble yields/restores topmost around fullscreen (reflection) ----
                Type speechT = asm.GetType("DesktopPet.FormSpeech", true);
                var speech = (Form)Activator.CreateInstance(speechT, true);
                try
                {
                    MethodInfo setSuppressed = speechT.GetMethod("SetFullscreenSuppressed", NpInstance);
                    setSuppressed.Invoke(speech, new object[] { true });
                    Check("speech bubble yields to fullscreen", !speech.TopMost);
                    setSuppressed.Invoke(speech, new object[] { false });
                    Check("speech bubble restores topmost after fullscreen", speech.TopMost);
                }
                finally { speech.Dispose(); }

                // ---- FormPet.ChildBudget per-root + process-global caps, reuse, prune (reflection) ----
                Type formT = asm.GetType("DesktopPet.FormPet", true);
                Type budgetT = formT.GetNestedType("ChildBudget", BindingFlags.NonPublic);
                MethodInfo tryAcquire = budgetT.GetMethod("TryAcquire", PubInstance);
                MethodInfo release = budgetT.GetMethod("Release", PubInstance);
                FieldInfo activeF = budgetT.GetField("active", NpInstance);

                object[] budgets = { Activator.CreateInstance(budgetT, true), Activator.CreateInstance(budgetT, true), Activator.CreateInstance(budgetT, true) };
                int[] held = { 0, 0, 0 };
                try
                {
                    for (int i = 0; i < 32; i++) { if (!(bool)tryAcquire.Invoke(budgets[0], new object[0])) throw new Exception("first root stopped at " + i); held[0]++; }
                    Check("per-root child cap", !(bool)tryAcquire.Invoke(budgets[0], new object[0]));
                    for (int i = 0; i < 32; i++) { if (!(bool)tryAcquire.Invoke(budgets[1], new object[0])) throw new Exception("process stopped at " + (i + 32)); held[1]++; }
                    Check("process-global child cap", !(bool)tryAcquire.Invoke(budgets[2], new object[0]));
                    release.Invoke(budgets[0], new object[0]); held[0]--;
                    Check("released global child slot reusable", (bool)tryAcquire.Invoke(budgets[2], new object[0])); held[2]++;
                }
                finally
                {
                    for (int b = 0; b < budgets.Length; b++) while (held[b] > 0) { release.Invoke(budgets[b], new object[0]); held[b]--; }
                }

                object parentForm = null;
                object pruneBudget = Activator.CreateInstance(budgetT, true);
                var disposedChildren = new System.Collections.Generic.List<IDisposable>();
                try
                {
                    parentForm = Activator.CreateInstance(formT);
                    FieldInfo childsF = formT.GetField("childs", NpInstance);
                    var childs = (System.Collections.IList)childsF.GetValue(parentForm);
                    ConstructorInfo childCtor = null;
                    foreach (ConstructorInfo c in formT.GetConstructors(NpInstance)) if (c.GetParameters().Length == 8) { childCtor = c; break; }
                    if (childCtor == null) throw new Exception("private 8-arg child FormPet constructor not found");

                    for (int i = 0; i < 2; i++)
                    {
                        if (!(bool)tryAcquire.Invoke(pruneBudget, new object[0])) throw new Exception("unable to reserve child slot " + i);
                        object child = childCtor.Invoke(new object[] { null, null, parentForm, Point.Empty, false, 0, 1, pruneBudget });
                        childs.Add(child);
                        ((IDisposable)child).Dispose();
                        disposedChildren.Add((IDisposable)child);
                    }

                    formT.GetMethod("PruneClosedChildren", NpInstance).Invoke(parentForm, new object[0]);
                    Check("adjacent disposed children pruned once", childs.Count == 0);
                    Check("disposed-child budget slots released", (int)activeF.GetValue(pruneBudget) == 0);
                }
                finally
                {
                    foreach (IDisposable child in disposedChildren) { try { child.Dispose(); } catch { } }
                    if (parentForm != null) { try { ((IDisposable)parentForm).Dispose(); } catch { } }
                    int activeSlots = (int)activeF.GetValue(pruneBudget);
                    while (activeSlots-- > 0) release.Invoke(pruneBudget, new object[0]);
                }

                // ---- ReadBoundedPetXml: BOM decode + oversized rejection (reflection) ----
                MethodInfo readBounded = formT.GetMethod("ReadBoundedPetXml", NpStatic);
                string tempFile = Path.Combine(Path.GetTempPath(), "DesktopPet-bounded-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    File.WriteAllText(tempFile, "<root />", new UTF8Encoding(true));
                    Check("bounded UTF-8 BOM decode", (string)readBounded.Invoke(null, new object[] { tempFile }) == "<root />");
                    using (FileStream fs = File.Open(tempFile, FileMode.Create, FileAccess.Write, FileShare.None)) fs.SetLength(PetXmlValidator.MaximumXmlBytes + 1L);
                    CheckRejects("maximum-plus-one XML read rejected", () => readBounded.Invoke(null, new object[] { tempFile }));
                }
                finally { try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { } }

                // Tile bleed on the smooth-downscale path. Scaling a sub-rectangle of a bigger bitmap makes
                // the interpolation kernel sample PAST the source rectangle, so a downscaled frame picked up
                // a column of the neighbouring tile: the reported dark line down the left edge of a pet's
                // fall frame. Only converted (alpha) pets being downscaled take that path.
                //
                // The fixture makes the bleed unmissable: tile 0 is solid black, tile 1 solid white, and
                // tile 1 is downscaled. Any dark pixel in the result came from across the boundary, because
                // nothing in tile 1 is dark.
                using (var sheet = new Bitmap(64, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(sheet))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.FillRectangle(Brushes.Black, 0, 0, 32, 32);
                        g.FillRectangle(Brushes.White, 32, 0, 32, 32);
                    }
                    using (Bitmap staged = Xml.StageOneTileForDiagnostics(sheet, 2, 1, 1, 16, 16, true))
                    {
                        int darkest = 255;
                        for (int y = 0; y < staged.Height; y++)
                            for (int x = 0; x < staged.Width; x++)
                            {
                                Color c = staged.GetPixel(x, y);
                                if (c.A == 0) continue;
                                if (c.R < darkest) darkest = c.R;
                            }
                        Check("smooth downscale does not sample across the tile boundary (darkest="
                              + darkest + ")", darkest >= 250);
                    }
                }

                // Drag swing. A converted pet's drag animation carries up to 7 poses, one per horizontal
                // offset band between its body and the cursor, and the pet SWINGS from your hand. Positional
                // lag cannot drive it (the drag branch snaps the pet's centre onto the cursor every tick, so
                // lag is always zero), so it is driven by cursor VELOCITY instead. Frame 0 is the body
                // trailing furthest LEFT, so moving the cursor RIGHT must select a LOW index.
                Check("swing: still cursor hangs at the centre pose",
                    FormPet.DragSwingFrameIndexFor(0.0, 7) == 3);
                Check("swing: cursor moving RIGHT trails the body left (frame 0)",
                    FormPet.DragSwingFrameIndexFor(40.0, 7) == 0);
                Check("swing: cursor moving LEFT trails the body right (last frame)",
                    FormPet.DragSwingFrameIndexFor(-40.0, 7) == 6);
                Check("swing: a gentle nudge does not jump straight to the extreme",
                    FormPet.DragSwingFrameIndexFor(4.0, 7) > 0 &&
                    FormPet.DragSwingFrameIndexFor(4.0, 7) < 3);
                Check("swing: the mapping is monotonic across the range",
                    FormPet.DragSwingFrameIndexFor(-18.0, 7) >= FormPet.DragSwingFrameIndexFor(-9.0, 7) &&
                    FormPet.DragSwingFrameIndexFor(-9.0, 7) >= FormPet.DragSwingFrameIndexFor(0.0, 7) &&
                    FormPet.DragSwingFrameIndexFor(0.0, 7) >= FormPet.DragSwingFrameIndexFor(9.0, 7) &&
                    FormPet.DragSwingFrameIndexFor(9.0, 7) >= FormPet.DragSwingFrameIndexFor(18.0, 7));
                // A single-frame drag is most Android bundles, and it must not index out of range.
                Check("swing: a single-pose drag stays on frame 0",
                    FormPet.DragSwingFrameIndexFor(999.0, 1) == 0 &&
                    FormPet.DragSwingFrameIndexFor(-999.0, 1) == 0);
                Check("swing: an absurd velocity clamps instead of overflowing",
                    FormPet.DragSwingFrameIndexFor(100000.0, 5) == 0 &&
                    FormPet.DragSwingFrameIndexFor(-100000.0, 5) == 4);

                // Gaze. A converted pet's "sit and look at the mouse" animation is tagged faceCursor, and the
                // host aims it as the animation starts. The comparison is against the CHARACTER's centre, not
                // the window's, so these assertions are about the rule and the insets are tested separately.
                //
                // The sign is the whole thing and it is easy to get backwards, so it is pinned in both
                // directions rather than asserted once: unmirrored sprite art is LEFT-facing, the engine
                // mirrors for rightward, and "cursor is left of me" therefore means "do not mirror".
                Check("gaze: a cursor left of the character faces left",
                    FormPet.ShouldFaceLeft(100.0, 500.0));
                Check("gaze: a cursor right of the character faces right",
                    !FormPet.ShouldFaceLeft(900.0, 500.0));
                // Dead centre must not flip on rounding noise. Either answer is defensible; what matters is
                // that it is STABLE, because a pet standing under the pointer would otherwise strobe.
                Check("gaze: a cursor exactly on the centre is stable",
                    FormPet.ShouldFaceLeft(500.0, 500.0) == FormPet.ShouldFaceLeft(500.0, 500.0) &&
                    !FormPet.ShouldFaceLeft(500.0, 500.0));
                Check("gaze: a character off the left of the screen still aims correctly",
                    !FormPet.ShouldFaceLeft(10.0, -120.0) && FormPet.ShouldFaceLeft(-300.0, -120.0));

                // Window EDGE discrimination. The host used to raise a bare WINDOW at all three window
                // borders, so a pet could not tell "I walked off the left side" from "I landed on the top".
                // It now raises WINDOW plus a discriminator, and these assertions pin both halves of the
                // bargain: the new values narrow, and the old one still catches everything.
                TNextAnimation.TOnly onLeft = TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_LEFT;
                TNextAnimation.TOnly onRight = TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_RIGHT;
                TNextAnimation.TOnly onTop = TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_TOP;

                // The compatibility half. 955 window edges ship in the hand-authored pets and every one of
                // them says `only="window"`; if any of these three went false those pets would stop
                // transitioning at a window and simply walk off it.
                Check("window: a generic window edge still fires at all three window borders",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW, onLeft) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW, onRight) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW, onTop));
                Check("window: horizontal+ still fires at a window border",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.HORIZONTAL_, onTop));

                // The discrimination half, stated as three exclusions rather than three inclusions, because
                // an implementation that simply matched everything would pass the inclusions.
                Check("window: a left-edge animation fires on the left edge only",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, onLeft) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, onRight) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, onTop));
                Check("window: a right-edge animation fires on the right edge only",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_RIGHT, onRight) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_RIGHT, onLeft) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_RIGHT, onTop));
                Check("window: a top-edge animation fires on the top edge only",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, onTop) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, onLeft) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, onRight));

                // A window edge must not leak into a SCREEN edge, which is a different situation entirely.
                Check("window: window edges do not fire at screen borders",
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, TNextAnimation.TOnly.VERTICAL) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, TNextAnimation.TOnly.HORIZONTAL) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_RIGHT, TNextAnimation.TOnly.TASKBAR));

                // NONE is "no flag given" and is taken everywhere. The three new bits all sit inside its
                // 0x7F mask, so a mistake here would silently turn every unconditional edge into a gated one.
                Check("window: an unconditional edge is still taken at every window border",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, onLeft) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, onRight) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, onTop) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, TNextAnimation.TOnly.NONE));

                // Vocabulary: the attribute the converter writes has to reach the flag the host matches.
                Check("window: the only= vocabulary maps to the right flags",
                    Xml.ParseOnlyFlag("window-left") == TNextAnimation.TOnly.WINDOW_LEFT &&
                    Xml.ParseOnlyFlag("window-right") == TNextAnimation.TOnly.WINDOW_RIGHT &&
                    Xml.ParseOnlyFlag("window-top") == TNextAnimation.TOnly.WINDOW_TOP &&
                    Xml.ParseOnlyFlag("window") == TNextAnimation.TOnly.WINDOW &&
                    Xml.ParseOnlyFlag("vertical") == TNextAnimation.TOnly.VERTICAL);
                // ...and the validator has to let such a pet in at all. These two lists are maintained
                // separately, and a value the parser understands but the validator refuses does not degrade:
                // it rejects the entire pet.
                Check("window: the validator accepts the window-edge vocabulary",
                    PetXmlValidator.IsAllowedOnly("window-left") &&
                    PetXmlValidator.IsAllowedOnly("window-right") &&
                    PetXmlValidator.IsAllowedOnly("window-top") &&
                    PetXmlValidator.IsAllowedOnly("window") &&
                    PetXmlValidator.IsAllowedOnly("vertical"));
                Check("window: the validator still refuses a value nothing implements",
                    !PetXmlValidator.IsAllowedOnly("window-bottom") &&
                    !PetXmlValidator.IsAllowedOnly("sideways"));

                if (ok) sb.AppendLine("PASS: focused runtime hardening regression harness.");
            }
            catch (Exception ex)
            {
                ok = false;
                Exception inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
                sb.AppendLine("EXC: " + inner.GetType().Name + ": " + inner.Message);
            }

            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-hardening-selftest.txt"), sb.ToString()); } catch { }
            return ok;
        }

        private static object MemberValue(Type t, object instance, string name)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(instance);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(instance, new object[0]);
            throw new MissingMemberException(t.FullName + "." + name);
        }
    }
}
