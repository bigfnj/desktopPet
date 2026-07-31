using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace DesktopPet
{
    internal sealed class PetTesterValidationSnapshot
    {
        public CheckState XmlState;
        public int XmlTag;
        public CheckState ResourceState;
        public int ResourceTag;
        public CheckState AnimationState;
        public int AnimationTag;
        public string Output;
    }

    internal static class PetTesterSelfTests
    {
        public static int Run(string petXmlPath)
        {
            try
            {
                TestCoordinatorCancellation();
                TestDroppedPetFileGate(petXmlPath);
                TestMalformedAndCompleteValidation(petXmlPath);
                Console.WriteLine(
                    "PASS: PetTester runtime self-test (local drag/drop path gate, " +
                    "in-flight supersession and close cancellation, current-only " +
                    "publication, malformed XML state, image decoding, full animation " +
                    "validation, parent-gated child reachability, and zero-probability " +
                    "transition reachability).");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                return 1;
            }
        }

        private static void TestCoordinatorCancellation()
        {
            var coordinator = new ValidationLoadCoordinator();
            ValidationLoadSession first = coordinator.Begin();
            ValidationLoadSession second = coordinator.Begin();
            Assert(
                first.Token.IsCancellationRequested,
                "Starting a second load did not cancel the stale load.");
            Assert(
                !coordinator.IsCurrent(first) && coordinator.IsCurrent(second),
                "The coordinator did not publish only the newest load.");

            coordinator.Dispose();
            Assert(
                second.Token.IsCancellationRequested,
                "Closing the owner did not cancel its active load.");
            Assert(
                !coordinator.IsCurrent(second),
                "A load remained publishable after coordinator disposal.");
        }

        private static void TestDroppedPetFileGate(string petXmlPath)
        {
            string canonicalPath;
            string error;
            Assert(
                Form1.TryResolveDroppedPetFile(
                    petXmlPath,
                    out canonicalPath,
                    out error),
                "The supplied local animations.xml was rejected by the drag/drop gate: " +
                error);
            Assert(
                string.Equals(
                    canonicalPath,
                    Path.GetFullPath(petXmlPath),
                    StringComparison.OrdinalIgnoreCase),
                "The drag/drop gate did not return the canonical local pet path.");

            Assert(
                !Form1.TryResolveDroppedPetFile(
                    @"\\pettester.invalid\share\animations.xml",
                    out canonicalPath,
                    out error),
                "The drag/drop gate accepted a UNC pet path.");
            Assert(
                string.IsNullOrEmpty(canonicalPath),
                "The rejected UNC pet path returned a canonical path.");
        }

        private static void TestMalformedAndCompleteValidation(string petXmlPath)
        {
            if (string.IsNullOrWhiteSpace(petXmlPath) ||
                !File.Exists(petXmlPath))
                throw new FileNotFoundException(
                    "A default animations.xml is required for the full validation self-test.",
                    petXmlPath);

            string completeXml = File.ReadAllText(
                petXmlPath,
                new UTF8Encoding(false, true));
            using (var form = new Form1())
            {
                IntPtr createdHandle = form.Handle;
                Assert(
                    createdHandle != IntPtr.Zero,
                    "The self-test form handle was not created.");
                PetTesterValidationSnapshot malformed =
                    WaitWithMessagePump(
                        form.RunValidationSelfTestAsync(
                            "<animations><broken></animations>"),
                        "malformed XML validation");
                Assert(
                    malformed.XmlState == CheckState.Checked &&
                    malformed.XmlTag == 2,
                    "The bounded UTF-8 stage did not remain successful for malformed XML.");
                Assert(
                    malformed.ResourceState == CheckState.Unchecked &&
                    malformed.ResourceTag == 0,
                    "Malformed XML did not reset the resource-validation state.");
                Assert(
                    malformed.AnimationState == CheckState.Unchecked &&
                    malformed.AnimationTag == 0,
                    "Malformed XML left animation validation in a stale state.");

                PetTesterValidationSnapshot complete =
                    WaitWithMessagePump(
                        form.RunValidationSelfTestAsync(completeXml),
                        "complete XML validation");
                Assert(
                    complete.XmlState == CheckState.Checked &&
                    complete.XmlTag == 2,
                    "Complete XML failed the bounded UTF-8 stage.");
                Assert(
                    complete.ResourceState == CheckState.Checked &&
                    complete.ResourceTag == 2,
                    "Complete XML failed schema or image/icon decoding.");
                Assert(
                    complete.AnimationState == CheckState.Checked &&
                    complete.AnimationTag == 2,
                    "Complete XML failed full animation/link validation.");

                TestUnreachableChildReachability(form, completeXml);
                TestZeroProbabilityTransitionReachability(form, completeXml);
            }

            TestInFlightSupersession(completeXml);
            TestFormCloseCancellation(completeXml);
        }

        private static void TestUnreachableChildReachability(
            Form1 form,
            string completeXml)
        {
            string fixtureXml = BuildUnreachableChildFixture(completeXml);
            PetTesterValidationSnapshot snapshot =
                WaitWithMessagePump(
                    form.RunValidationSelfTestAsync(fixtureXml),
                    "unreachable child validation");

            Assert(
                snapshot.AnimationState == CheckState.Checked &&
                snapshot.AnimationTag == 2,
                "The unreachable-child fixture failed animation validation.");
            Assert(
                snapshot.Output.IndexOf(
                    "ANIMATION WARNING: On animation 1000: " +
                    "This ID is never played.",
                    StringComparison.Ordinal) >= 0,
                "An unreachable child parent was not reported as never played.");
            Assert(
                snapshot.Output.IndexOf(
                    "ANIMATION WARNING: On animation 1001: " +
                    "This ID is never played.",
                    StringComparison.Ordinal) >= 0,
                "A child target was treated as reachable before its parent ran.");
        }

        private static void TestZeroProbabilityTransitionReachability(
            Form1 form,
            string completeXml)
        {
            string fixtureXml =
                BuildZeroProbabilityTransitionFixture(completeXml);
            PetTesterValidationSnapshot snapshot =
                WaitWithMessagePump(
                    form.RunValidationSelfTestAsync(fixtureXml),
                    "zero-probability transition validation");

            Assert(
                snapshot.AnimationState == CheckState.Checked &&
                snapshot.AnimationTag == 2,
                "The zero-probability transition fixture failed animation validation.");
            Assert(
                snapshot.Output.IndexOf(
                    "ANIMATION WARNING: On animation 1002: " +
                    "This ID is never played.",
                    StringComparison.Ordinal) >= 0,
                "A zero-probability transition made its target reachable.");
        }

        private static string BuildZeroProbabilityTransitionFixture(
            string completeXml)
        {
            var document = new XmlDocument();
            document.LoadXml(completeXml);
            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace(
                "p",
                document.DocumentElement.NamespaceURI);

            XmlElement animations = document.SelectSingleNode(
                "/p:animations/p:animations",
                namespaces) as XmlElement;
            XmlElement sourceAnimation = document.SelectSingleNode(
                "/p:animations/p:animations/p:animation[@id='1']",
                namespaces) as XmlElement;
            XmlElement sourceSequence = sourceAnimation == null
                ? null
                : sourceAnimation.SelectSingleNode(
                    "p:sequence",
                    namespaces) as XmlElement;
            Assert(
                animations != null &&
                sourceAnimation != null &&
                sourceSequence != null,
                "The complete XML did not contain the zero-probability fixture nodes.");

            AppendUnreachableAnimation(
                animations,
                sourceAnimation,
                namespaces,
                "1002",
                "zero-probability-target");

            XmlElement zeroProbabilityTransition = document.CreateElement(
                "next",
                document.DocumentElement.NamespaceURI);
            zeroProbabilityTransition.SetAttribute("probability", "0");
            zeroProbabilityTransition.InnerText = "1002";
            sourceSequence.AppendChild(zeroProbabilityTransition);

            return document.OuterXml;
        }

        private static string BuildUnreachableChildFixture(string completeXml)
        {
            var document = new XmlDocument();
            document.LoadXml(completeXml);
            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace(
                "p",
                document.DocumentElement.NamespaceURI);

            XmlElement animations = document.SelectSingleNode(
                "/p:animations/p:animations",
                namespaces) as XmlElement;
            XmlElement sourceAnimation = document.SelectSingleNode(
                "/p:animations/p:animations/p:animation[@id='1']",
                namespaces) as XmlElement;
            XmlElement children = document.SelectSingleNode(
                "/p:animations/p:childs",
                namespaces) as XmlElement;
            XmlElement sourceChild = document.SelectSingleNode(
                "/p:animations/p:childs/p:child[1]",
                namespaces) as XmlElement;
            Assert(
                animations != null &&
                sourceAnimation != null &&
                children != null &&
                sourceChild != null,
                "The complete XML did not contain the fixture source nodes.");

            AppendUnreachableAnimation(
                animations,
                sourceAnimation,
                namespaces,
                "1000",
                "unreachable-child-parent");
            AppendUnreachableAnimation(
                animations,
                sourceAnimation,
                namespaces,
                "1001",
                "unreachable-child-target");

            XmlElement child =
                (XmlElement)sourceChild.CloneNode(true);
            child.SetAttribute("animationid", "1000");
            XmlElement childNext =
                child.SelectSingleNode("p:next", namespaces) as XmlElement;
            Assert(
                childNext != null,
                "The fixture child did not contain a next animation.");
            childNext.InnerText = "1001";
            children.AppendChild(child);

            return document.OuterXml;
        }

        private static void AppendUnreachableAnimation(
            XmlElement animations,
            XmlElement sourceAnimation,
            XmlNamespaceManager namespaces,
            string id,
            string name)
        {
            XmlElement animation =
                (XmlElement)sourceAnimation.CloneNode(true);
            animation.SetAttribute("id", id);
            XmlElement animationName =
                animation.SelectSingleNode("p:name", namespaces) as XmlElement;
            Assert(
                animationName != null,
                "The fixture animation did not contain a name.");
            animationName.InnerText = name;
            foreach (XmlNode next in animation.SelectNodes(
                ".//p:next",
                namespaces))
            {
                next.InnerText = id;
            }
            animations.AppendChild(animation);
        }

        private static void TestInFlightSupersession(string completeXml)
        {
            using (var form = new Form1())
            using (var workerStarted = new ManualResetEventSlim(false))
            {
                IntPtr createdHandle = form.Handle;
                Assert(
                    createdHandle != IntPtr.Zero,
                    "The supersession self-test form handle was not created.");

                int probeCalls = 0;
                bool cancellationObserved = false;
                form.ValidationWorkProbeForTest = delegate(CancellationToken token)
                {
                    if (Interlocked.Increment(ref probeCalls) != 1) return;
                    workerStarted.Set();
                    while (!token.IsCancellationRequested) Thread.Sleep(1);
                    cancellationObserved = true;
                    token.ThrowIfCancellationRequested();
                };

                Task<long> stale =
                    form.RunValidationOperationForTestAsync(completeXml);
                Assert(
                    WaitForSignalWithMessagePump(
                        workerStarted,
                        "the superseded validation worker"),
                    "The superseded validation worker did not start.");
                form.ValidationWorkProbeForTest = null;

                Task<long> current =
                    form.RunValidationOperationForTestAsync(completeXml);
                long currentGeneration = WaitWithMessagePump(
                    current,
                    "current validation after supersession");
                long staleGeneration = WaitWithMessagePump(
                    stale,
                    "superseded validation cancellation");

                Assert(
                    cancellationObserved,
                    "The in-flight superseded worker did not observe cancellation.");
                Assert(
                    staleGeneration < currentGeneration,
                    "Validation generations were not ordered across supersession.");
                Assert(
                    form.LastPublishedValidationGeneration == currentGeneration,
                    "A stale validation generation published after its replacement.");
                PetTesterValidationSnapshot snapshot =
                    form.CaptureValidationSnapshotForTest();
                Assert(
                    snapshot.ResourceState == CheckState.Checked &&
                    snapshot.AnimationState == CheckState.Checked,
                    "The current validation result was not published after supersession.");
            }
        }

        private static void TestFormCloseCancellation(string completeXml)
        {
            using (var form = new Form1())
            using (var workerStarted = new ManualResetEventSlim(false))
            {
                IntPtr createdHandle = form.Handle;
                Assert(
                    createdHandle != IntPtr.Zero,
                    "The close-cancellation self-test form handle was not created.");

                bool cancellationObserved = false;
                form.ValidationWorkProbeForTest = delegate(CancellationToken token)
                {
                    workerStarted.Set();
                    while (!token.IsCancellationRequested) Thread.Sleep(1);
                    cancellationObserved = true;
                    token.ThrowIfCancellationRequested();
                };

                Task<long> active =
                    form.RunValidationOperationForTestAsync(completeXml);
                Assert(
                    WaitForSignalWithMessagePump(
                        workerStarted,
                        "the close-cancelled validation worker"),
                    "The close-cancelled validation worker did not start.");
                form.Close();
                WaitWithMessagePump(
                    active,
                    "in-flight validation cancellation during form close");
                Assert(
                    cancellationObserved,
                    "Closing the form did not cancel its in-flight validation worker.");
                Assert(
                    form.LastPublishedValidationGeneration == 0,
                    "A validation result published after form closure.");
            }
        }

        private static bool WaitForSignalWithMessagePump(
            ManualResetEventSlim signal,
            string operation)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (!signal.IsSet)
            {
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException(
                        "Timed out while waiting for " + operation + ".");
                Application.DoEvents();
                Thread.Sleep(1);
            }
            return true;
        }

        private static T WaitWithMessagePump<T>(
            Task<T> task,
            string operation)
        {
            if (task == null) throw new ArgumentNullException("task");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException(
                        "Timed out while waiting for " + operation + ".");
                Application.DoEvents();
                Thread.Sleep(1);
            }
            return task.GetAwaiter().GetResult();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
