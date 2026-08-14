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
                    using (FileStream fs = File.Open(tempFile, FileMode.Create, FileAccess.Write, FileShare.None)) fs.SetLength(4194305);
                    CheckRejects("maximum-plus-one XML read rejected", () => readBounded.Invoke(null, new object[] { tempFile }));
                }
                finally { try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { } }

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
