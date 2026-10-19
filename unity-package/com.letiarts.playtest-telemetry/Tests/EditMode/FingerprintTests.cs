using NUnit.Framework;

namespace LetiArts.PlaytestTelemetry.Tests
{
    public class FingerprintTests
    {
        const string StackA = "Game.Enemy:Update () (at Assets/Scripts/Enemy.cs:42)\nGame.Level:Tick () (at Assets/Scripts/Level.cs:10)";

        // ---- hash vectors (FNV-1a 64-bit, published test vectors)

        [TestCase("", "cbf29ce484222325")]
        [TestCase("a", "af63dc4c8601ec8c")]
        [TestCase("foobar", "85944171f73967e8")]
        public void Fnv1a_KnownVectors(string input, string expectedHex)
        {
            Assert.AreEqual(expectedHex, Fnv1a.ToHex(Fnv1a.HashUtf8(input)));
        }

        [Test]
        public void Fnv1a_IsStableAndHexIs16Chars()
        {
            string hex = Fnv1a.ToHex(Fnv1a.HashUtf8("héllo 日本"));
            Assert.AreEqual(16, hex.Length);
            Assert.AreEqual(hex, Fnv1a.ToHex(Fnv1a.HashUtf8("héllo 日本")));
            // UTF-8 bytes of "é" are C3 A9, so this must differ from the Latin-1 interpretation.
            Assert.AreNotEqual(Fnv1a.ToHex(Fnv1a.HashUtf8("hello")), hex);
        }

        // ---- normalization

        [TestCase("Found 3 items", "Found <n> items")]
        [TestCase("Took 12.5 ms", "Took <n> ms")]
        [TestCase("User 'bob' not found", "User <str> not found")]
        [TestCase("Failed to load \"Assets/a.png\" now", "Failed to load <str> now")]
        [TestCase("don't panic 5", "don't panic <n>")]
        [TestCase("Error at 0x1F4A", "Error at <hex>")]
        [TestCase("id=550e8400-e29b-41d4-a716-446655440000 gone", "id=<guid> gone")]
        [TestCase("Vector3 is 4", "Vector3 is <n>")]
        [TestCase("File Assets/Scripts/Foo.cs missing", "File <path> missing")]
        [TestCase("File Assets/Foo.cs missing", "File <path> missing")]
        [TestCase("Loading C:\\Users\\bob\\level1.json failed", "Loading <path> failed")]
        [TestCase("choose and/or pick", "choose and/or pick")]
        [TestCase("Pos (1.5, -2)", "Pos (<n>, -<n>)")]
        [TestCase("", "")]
        public void NormalizeMessage_Table(string input, string expected)
        {
            Assert.AreEqual(expected, Fingerprinter.NormalizeMessage(input));
        }

        [Test]
        public void NormalizeMessage_IsTruncated()
        {
            Assert.LessOrEqual(Fingerprinter.NormalizeMessage(new string('x', 5000)).Length, Fingerprinter.MaxTemplateLength);
        }

        // ---- same bug => same fingerprint

        [TestCase("NullReferenceException: slot 7", "NullReferenceException: slot 9")]
        [TestCase("Cannot load 'Assets/Textures/a.png' (id 123)", "Cannot load 'Assets/Textures/b.png' (id 456)")]
        [TestCase("Request 6f1c2a3e-1111-2222-3333-444455556666 failed at 0xDEADBEEF", "Request 00000000-aaaa-bbbb-cccc-ddddeeeeffff failed at 0x1234")]
        [TestCase("Loading C:\\Users\\bob\\game\\level1.json failed", "Loading C:\\Users\\amy\\other\\level2.json failed")]
        [TestCase("Took 12.5 ms", "Took 300.25 ms")]
        public void SameBug_DifferentValues_SameFingerprint(string a, string b)
        {
            Assert.AreEqual(Fingerprinter.Compute(a, StackA), Fingerprinter.Compute(b, StackA));
        }

        [Test]
        public void SameBug_LineNumbersAndEngineFramesIgnored()
        {
            const string withEngine =
                "UnityEngine.Debug:LogError (object)\nGame.Enemy:Update () (at Assets/Scripts/Enemy.cs:99)\nGame.Level:Tick () (at Assets/Scripts/Level.cs:10)";
            Assert.AreEqual(Fingerprinter.Compute("boom", StackA), Fingerprinter.Compute("boom", withEngine));
        }

        // ---- different bug => different fingerprint

        [Test]
        public void DifferentCallSites_DifferentFingerprints()
        {
            const string other = "Game.Player:Update () (at Assets/Scripts/Player.cs:42)\nGame.Level:Tick () (at Assets/Scripts/Level.cs:10)";
            Assert.AreNotEqual(Fingerprinter.Compute("boom", StackA), Fingerprinter.Compute("boom", other));
        }

        [Test]
        public void DifferentMessages_DifferentFingerprints()
        {
            Assert.AreNotEqual(Fingerprinter.Compute("boom", StackA), Fingerprinter.Compute("bang", StackA));
        }

        // ---- stack frames

        [Test]
        public void TopFrames_SkipsEngineFramesAndKeepsThree()
        {
            const string stack =
                "UnityEngine.Debug:LogError (object)\n" +
                "System.Threading.Thread:Run ()\n" +
                "A.B:One () (at x.cs:1)\n" +
                "at C.D.Two (System.Int32 x) [0x00012] in <abc>:0\n" +
                "E.F:Three ()\n" +
                "G.H:Four ()";
            var frames = Fingerprinter.TopFrames(stack, 3);
            CollectionAssert.AreEqual(new[] { "A.B:One", "C.D.Two", "E.F:Three" }, frames);
        }

        [Test]
        public void TopFrames_EmptyStack()
        {
            Assert.AreEqual(0, Fingerprinter.TopFrames("", 3).Count);
            Assert.AreEqual(0, Fingerprinter.TopFrames(null, 3).Count);
        }
    }
}
