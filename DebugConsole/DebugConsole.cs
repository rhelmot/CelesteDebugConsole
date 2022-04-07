using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using FMOD.Studio;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Mono.CSharp;
using Monocle;
using MonoMod.Cil;

namespace Celeste.Mod.DebugConsole {
    public class DebugConsole : EverestModule {
        #region everest setup
        private static readonly FieldInfo CommandHistoryFieldInfo =
            typeof(Monocle.Commands).GetField("commandHistory", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Regex SeparatorRegex = new Regex(@"^evalcs[ |,]+", RegexOptions.Compiled);
        public static DebugConsole Instance;
        public bool CaptureInput = false;
        public string Prompt = "C#>";
        public List<string> History = new List<string>();
        public int HistoryIndex = 0;
        public string SavedLine = "";
        public List<Tuple<string, Func<object>>> Watches = new List<Tuple<string, Func<object>>>();

        [Command("cs", "Start C# interactive session (Debug Console)")]
        public static void StartCapture() {
            Instance.CaptureInput = true;
            Instance.SetText(Instance.SavedLine);
            Engine.Commands.Log("Welcome to the C# interactive prompt. Ctrl-C to clear line and Ctrl-D to exit.", Color.GreenYellow);
        }

        [Command("evalcs", "Evaluate C# codes (Debug Console)")]
        public static void EvalCsCommand(string codes) {
            List<string> commandHistory = CommandHistoryFieldInfo.GetValue(Engine.Commands) as List<string>;
            if (commandHistory?.FirstOrDefault()?.StartsWith("evalcs") == true) {
                codes = SeparatorRegex.Replace(commandHistory[0], "");
            }
            Instance.HandleLine(codes);
        }

        public DebugConsole() {
            Instance = this;
        }

        public override void LoadContent(bool firstLoad) {
            this.Setup();
        }

        public override void Load() {
            On.Monocle.Commands.HandleKey += this.HandleDebugKeystroke;
            IL.Monocle.Commands.Render += this.CustomPrompt;
            On.Monocle.Engine.RenderCore += this.RenderHook;
        }

        public override void Unload() {
            On.Monocle.Commands.HandleKey -= this.HandleDebugKeystroke;
            IL.Monocle.Commands.Render -= this.CustomPrompt;
            On.Monocle.Engine.RenderCore -= this.RenderHook;
        }

        public override void CreateModMenuSection(TextMenu menu, bool inGame, EventInstance snapshot) {
        }

        public int GetIndex() {
            return (int)typeof(Monocle.Commands).GetField("charIndex", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Engine.Commands);
        }

        public void SetIndex(int idx) {
            typeof(Monocle.Commands).GetField("charIndex", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(Engine.Commands, idx);
        }

        public string PopText() {
            var field = typeof(Monocle.Commands).GetField("currentText", BindingFlags.NonPublic | BindingFlags.Instance);
            string text = (string) field.GetValue(Engine.Commands);
            field.SetValue(Engine.Commands, "");
            this.SetIndex(0);
            return text;
        }

        public void SetText(string value) {
            var field = typeof(Monocle.Commands).GetField("currentText", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(Engine.Commands, value);
            this.SetIndex(value.Length);
        }

        public void HandleDebugKeystroke(On.Monocle.Commands.orig_HandleKey orig, Monocle.Commands self, Keys key) {
            if (this.CaptureInput) {
                var extraState = Keyboard.GetState();
                var ctrl = extraState[Keys.LeftControl] == KeyState.Down || extraState[Keys.RightControl] == KeyState.Down;
                switch (key) {
                    case Keys.Enter:
                        var line = this.PopText();
                        this.HandleLine(line);
                        // do we actually want this behavior to be conditional...
                        if (this.HistoryIndex == this.History.Count || line != this.History[this.HistoryIndex]) {
                            this.History.Add(line);
                        }
                        this.HistoryIndex = this.History.Count;
                        return;
                    case Keys.C:
                        if (ctrl) {
                            this.PopText();
                            this.HandleCancel();
                            return;
                        }
                        break;
                    case Keys.D:
                        if (ctrl) {
                            this.SavedLine = this.PopText();
                            this.CaptureInput = false;
                            return;
                        }
                        break;
                    case Keys.Down:
                    case Keys.Up:
                        var dir = key == Keys.Up ? -1 : 1;
                        this.HistoryIndex = Calc.Clamp(this.HistoryIndex + dir, 0, this.History.Count);
                        this.SetText(this.HistoryIndex == this.History.Count ? "" : this.History[this.HistoryIndex]);
                        return;
                    case Keys.Tab:
                        var idx = this.GetIndex();
                        var txt = this.PopText();
                        var prefix = txt.Substring(0, idx);
                        var suffix = txt.Substring(idx);
                        var result = this.HandleTab(prefix);
                        this.SetText(result + suffix);
                        this.SetIndex(result.Length);
                        return;
                }
            }
            orig(self, key);
        }

        public void CustomPrompt(ILContext il) {
            var cursor = new ILCursor(il);
            while (cursor.TryGotoNext(MoveType.After, insn => insn.MatchLdstr(">"))) {
                cursor.EmitDelegate<Func<string, string>>(oldString => this.CaptureInput ? this.Prompt : oldString);
            }
        }

        private void RenderHook(On.Monocle.Engine.orig_RenderCore orig, Engine self) {
            orig(self);

            Draw.SpriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp,
                DepthStencilState.Default,
                RasterizerState.CullNone,
                null,
                Engine.ScreenMatrix);

            var position = new Vector2(1850, 30);

            foreach (var watch in Instance.Watches) {
                string txt;
                try {
                    txt = Fmt(watch.Item2());
                } catch (Exception) {
                    txt = "#ERROR";
                }

                txt = $"{watch.Item1}: {txt}";

                ActiveFont.DrawOutline(txt, position, new Vector2(1, 0), Vector2.One * 0.5f, Color.White, 2f, Color.Black);
                position += Vector2.UnitY * 40;
            }

            Draw.SpriteBatch.End();
        }

        #endregion

        public Evaluator Eval;
        public DebugWriter ErrPrinter;

        public void Setup() {
            this.ErrPrinter = new DebugWriter();
            var ctx = new CompilerContext(new CompilerSettings(), new StreamReportPrinter(this.ErrPrinter));
            this.Eval = new Evaluator(ctx);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                var name = asm.GetName().Name;
                switch (name) {
                    case "System":
                    case "System.Core":
                    case "mscorlib":
                        continue;
                }
                this.Eval.ReferenceAssembly(asm);
            }

            this.ErrPrinter.Intercept = true;
            this.Eval.Run("using Celeste;");
            this.Eval.Run("using Monocle;");
            this.Eval.Run("using Celeste.Mod.DebugConsole;");
            this.Eval.Run("using Microsoft.Xna.Framework;");
            this.Eval.Run("using Microsoft.Xna.Framework.Input;");
            this.Eval.Run("using System;");
            this.Eval.Run("using System.Collections;");
            this.Eval.Run("using System.Collections.Generic;");
            this.Eval.Run("using System.Linq;");
            this.Eval.Run("Action<object> log = (o) => DebugConsole.Log(o);");
            this.Eval.Run("Action<string, Func<object>> watch = (name, func) => DebugConsole.Watch(name, func);");
            this.Eval.Run("Action<string> unwatch = (name) => DebugConsole.Unwatch(name);");
            this.ErrPrinter.Intercept = false;
        }

        public void HandleLine(string line) {
            Engine.Commands.Log(line, Color.Aqua);
            try {
                var obj = this.Eval.Evaluate(line);
                Log(obj);
            } catch (Exception e) {
                //Engine.Commands.Log($"{e.GetType().Name}: {e.Message}", Color.Red);
                if (e.Message != "The expression failed to resolve") {
                    Engine.Commands.Log(e.Message, Color.Yellow);
                }
            }
        }

        private string HandleTab(string line) {
            var possibilities = this.Eval.GetCompletions(line, out var prefix);
            if (possibilities == null) {
                return line;
            }

            string bestPrefix = "";
            if (possibilities.Length != 0) {
                bestPrefix = new string(
                    possibilities.First().Substring(0, possibilities.Min(s => s.Length))
                        .TakeWhile((c, i) => possibilities.All(s => s[i] == c)).ToArray());

                if (bestPrefix == "") {
                    Engine.Commands.Log("==> " + string.Join(" ", possibilities.Select(x => prefix + x)));
                }
            }
            return line + bestPrefix;
        }

        public void HandleCancel() {
        }

        public static string Fmt(params object[] objs) {
            var builder = new StringBuilder();
            var first = true;
            foreach (var obj in objs) {
                if (!first) {
                    builder.Append(" ");
                }
                first = false;
                if (obj == null) {
                    builder.Append("null");
                } else if (obj is string objStr) {
                    builder.Append('"' + objStr
                                           .Replace("\\", "\\\\")
                                           .Replace("\"", "\\\"")
                                           .Replace("\n", "\\n")
                                           .Replace("\t", "\\t")
                                       + '"');
                } else {
                    builder.Append(obj);
                }
            }

            return builder.ToString();
        }

        public static void Log(params object[] objs) {
            Engine.Commands.Log(Fmt(objs));
        }

        public static void Watch(string name, Func<object> evaluator) {
            foreach (var watch in Instance.Watches) {
                if (watch.Item1 == name) {
                    throw new Exception("Name is already taken");
                }
            }

            Instance.Watches.Add(Tuple.Create(name, evaluator));
        }

        public static void Unwatch(string name) {
            var idx = 0;
            foreach (var watch in Instance.Watches) {
                if (watch.Item1 == name) {
                    Instance.Watches.RemoveAt(idx);
                    return;
                }

                idx++;
            }

            throw new Exception("No such watch");
        }
    }

    public class DebugWriter : TextWriter {
        public override Encoding Encoding { get; }

        private List<char> Buffer = new List<char>();
        public Color Color = Color.Yellow;
        public bool Intercept = false;

        public override void Write(char value) {
            if (this.Intercept) {
                return;
            }
            if (value == '\n') {
                Engine.Commands.Log(new string (this.Buffer.ToArray()), this.Color);
                this.Buffer.Clear();
            } else {
                this.Buffer.Add(value);
            }
        }
    }

    public static class Extensions {
        public static void LogAll<T>(this IEnumerable<T> self) {
            var idx = 0;
            foreach (var item in self) {
                DebugConsole.Log(idx, item);
                idx++;
            }
        }
    }
}