using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace UI
{
    /// <summary>
    /// Drives the MainMenu scene.
    /// 1. Player types a theme word.
    /// 2. Presses Play.
    /// 3. OllamaClient generates SD prompts from the theme (if available).
    /// 4. Plan JSON is saved to PlayerPrefs; theme is saved to PlayerPrefs.
    /// 5. Game scene is loaded.
    ///
    /// If Ollama is unavailable or fails, the pipeline falls back to
    /// hard-coded prompts via <see cref="FallbackPlan"/>.
    /// </summary>
    public class MainMenuController : MonoBehaviour
    {
        // ── PlayerPrefs keys (shared with AiLevelPipeline) ────────────────────
        public const string ThemePrefKey = "AI_Theme";
        public const string PlanPrefKey  = "AI_LevelPlan";

        [Header("Scene")]
        [Tooltip("Name of the scene to load when Play is pressed.")]
        public string gameSceneName = "Game";

        [Header("UI References")]
        public TMP_InputField themeInput;
        public TMP_Text       statusLabel;
        public GameObject     loadingPanel;

        [Header("Difficulty")]
        [Tooltip("Optional label showing the selected difficulty.")]
        public TMP_Text difficultyLabel;
        public UnityEngine.UI.Button easyButton;
        public UnityEngine.UI.Button normalButton;
        public UnityEngine.UI.Button hardButton;

        [Header("Ollama (optional)")]
        [Tooltip("Assign an OllamaClient component. If null, only FallbackPlan is used.")]
        public OllamaClient ollamaClient;

        private bool _busy = false;
        private Difficulty _selectedDifficulty = Difficulty.Normal;

        // ── Difficulty callbacks ───────────────────────────────────────────────

        public void OnEasyPressed()   => SelectDifficulty(Difficulty.Easy);
        public void OnNormalPressed() => SelectDifficulty(Difficulty.Normal);
        public void OnHardPressed()   => SelectDifficulty(Difficulty.Hard);

        private void SelectDifficulty(Difficulty d)
        {
            _selectedDifficulty = d;
            if (difficultyLabel != null) difficultyLabel.text = $"Difficulty: {d}";
        }

        // ── Play button callback ───────────────────────────────────────────────

        /// <summary>Called by the Play button's OnClick event.</summary>
        public void OnPlayPressed()
        {
            if (_busy) return;

            string theme = themeInput != null ? themeInput.text.Trim() : "forest";
            
            // Validate theme input
            string validationError = ValidateThemeInput(theme);
            if (!string.IsNullOrEmpty(validationError))
            {
                SetStatus(validationError);
                return;
            }

            StartCoroutine(PlayFlow());
        }

        // ── Flow ───────────────────────────────────────────────────────────────

        private IEnumerator PlayFlow()
        {
            _busy = true;

            Game.GameSessionState.SetDifficulty(_selectedDifficulty);
            string theme = themeInput != null
                ? themeInput.text.Trim()
                : "forest";

            if (string.IsNullOrWhiteSpace(theme)) theme = "forest";

            Game.GameSessionState.BeginRun(theme);
            string effectiveTheme = BuildEffectiveThemeForCurrentLevel();
            var fallbackPlan = FallbackPlan.For(
                Game.GameSessionState.BaseTheme,
                Game.GameSessionState.CurrentLevelIndex,
                Game.GameSessionState.RunSeed);

            // Save theme immediately (pipeline reads this first)
            PlayerPrefs.SetString(ThemePrefKey, effectiveTheme);
            PlayerPrefs.SetString(PlanPrefKey, JsonUtility.ToJson(fallbackPlan));
            PlayerPrefs.Save();

            // Try Ollama: lore + plan in parallel
            if (ollamaClient != null)
            {
                SetStatus("Asking AI for story and level plan...");
                SetLoading(true);

                LevelPlan plan = null;
                string planError = null;
                bool planDone = false;

                LoreData lore = null;
                bool loreDone = false;

                StartCoroutine(ollamaClient.GeneratePlan(
                    effectiveTheme,
                    p => { plan = p; planDone = true; },
                    e => { planError = e; planDone = true; }));

                StartCoroutine(ollamaClient.GenerateLore(
                    Game.GameSessionState.BaseTheme,
                    l => { lore = l; loreDone = true; },
                    _ => { loreDone = true; }));

                yield return new WaitUntil(() => planDone && loreDone);

                if (plan != null && plan.IsValid)
                {
                    string json = JsonUtility.ToJson(plan);
                    PlayerPrefs.SetString(PlanPrefKey, json);
                    PlayerPrefs.Save();
                    Debug.Log($"[MainMenuController] Ollama plan saved ({json.Length} chars).");
                }
                else
                {
                    Debug.LogWarning($"[MainMenuController] Ollama plan failed: {planError}. Using FallbackPlan.");
                }

                Game.GameSessionState.SetLore(lore ?? FallbackLore.For(Game.GameSessionState.BaseTheme));
                SetStatus("Starting game scene...");
            }
            else
            {
                Debug.Log("[MainMenuController] No OllamaClient - using FallbackPlan + FallbackLore.");
                Game.GameSessionState.SetLore(FallbackLore.For(Game.GameSessionState.BaseTheme));
                SetStatus("Starting game scene...");
            }

            // ── Load Game scene ────────────────────────────────────────────────
            SceneManager.LoadScene(gameSceneName);
        }

        private static string BuildEffectiveThemeForCurrentLevel()
        {
            string baseTheme = Game.GameSessionState.BaseTheme;
            string variant = ThemeVariationComposer.ComposeVariantTag(
                baseTheme,
                Game.GameSessionState.CurrentLevelIndex,
                Game.GameSessionState.RunSeed);
            return $"{baseTheme}, {variant}";
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void SetStatus(string msg)
        {
            if (statusLabel != null) statusLabel.text = msg;
        }

        private void SetLoading(bool show)
        {
            if (loadingPanel != null) loadingPanel.SetActive(show);
        }

        // ── AI Preview (section 8) ─────────────────────────────────────────────

        private GameObject _aiPreviewPanel;

        private void Start()
        {
            EnsureAiInfoButtonExists();
        }

        // ── Validation ──────────────────────────────────────────────────────────

        private static readonly string[] BlockedThemeTerms =
        {
            // Violence / gore
            "kill", "killing", "murder", "dead", "death", "corpse", "blood", "gore", "dismember", "behead",
            "execution", "massacre", "torture", "decapitate", "slaughter",
            // Sexual / abuse
            "rape", "rapist", "sexual", "sex", "porn", "porno", "xxx", "nude", "nudity", "fetish",
            "pedophile", "pedophilia", "molest", "incest", "bestiality",
            // Self-harm
            "suicide", "selfharm", "self-harm", "cutting", "overdose",
            // Hate / extremist
            "nazi", "hitler", "racist", "racism", "genocide", "terrorist", "terrorism",
            // Weapons / bombing
            "bomb", "explosive", "gun", "shooting", "weapon", "grenade",
            // Child abuse phrases
            "dead children", "child abuse", "child porn", "harm children"
        };

        /// <summary>Validates theme input. Returns error message if invalid, empty string if valid.</summary>
        private string ValidateThemeInput(string theme)
        {
            if (string.IsNullOrWhiteSpace(theme))
                return "Theme cannot be empty.";

            if (theme.Length > 100)
                return $"Theme too long ({theme.Length} chars, max 100).";

            // Check for newlines or problematic characters
            if (theme.Contains("\n") || theme.Contains("\r") || theme.Contains("\t"))
                return "Theme contains invalid characters.";

            // Check for excessive special characters (limit to ~20% of string)
            int specialCharCount = 0;
            foreach (char c in theme)
                if (!char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c))
                    specialCharCount++;
            
            if (specialCharCount > theme.Length * 0.2f)
                return "Theme contains too many special characters.";

            if (ContainsBlockedContent(theme))
                return "Theme contains inappropriate content.";

            // Heuristic checks (no hardcoded blacklist)
            if (!IsThemeSafe(theme))
                return "Theme contains inappropriate content.";

            return ""; // Valid
        }

        /// <summary>Heuristic checks for suspicious/inappropriate content (no blacklist).</summary>
        private static bool IsThemeSafe(string theme)
        {
            string lower = theme.ToLower();

            // 1. Excessive character repetition (aaa, xxx, 111)
            foreach (char c in "abcdefghijklmnopqrstuvwxyz0123456789")
            {
                string repeated = new string(c, 3);
                if (lower.Contains(repeated))
                    return false;
            }

            // 2. All uppercase (screaming) with punctuation (!!! ???)
            int upperCount = 0;
            int punctCount = 0;
            foreach (char c in theme)
            {
                if (char.IsUpper(c)) upperCount++;
                if (c == '!' || c == '?' || c == '*') punctCount++;
            }
            if (upperCount > theme.Length * 0.7f && punctCount > 2)
                return false;

            // 3. URL or email patterns (http://, @, etc.)
            if (lower.Contains("http") || lower.Contains("www") || lower.Contains("@") || lower.Contains(".com"))
                return false;

            // 4. SQL-like patterns (common injection attempts)
            if (lower.Contains("select") || lower.Contains("drop") || lower.Contains("insert") || 
                lower.Contains("delete") || lower.Contains("union"))
                return false;

            // 5. Numbers > 50% of length (likely spam/codes)
            int digitCount = 0;
            foreach (char c in theme)
                if (char.IsDigit(c)) digitCount++;
            if (digitCount > theme.Length * 0.5f)
                return false;

            // 6. Excessive punctuation
            int punct = 0;
            foreach (char c in theme)
                if (!char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c))
                    punct++;
            if (punct > 5)
                return false;

            // 7. Very short words that are often problematic (single letters/numbers only)
            string[] words = theme.Split(' ');
            foreach (string word in words)
            {
                if (word.Length > 0 && !HasLetters(word))
                    return false; // Pure numbers or symbols
            }

            return true;
        }

        private static bool ContainsBlockedContent(string theme)
        {
            string lower = theme.ToLower();
            string padded = " " + lower + " ";

            foreach (string term in BlockedThemeTerms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;

                // Multi-word phrases can be matched directly.
                if (term.Contains(" "))
                {
                    if (lower.Contains(term)) return true;
                    continue;
                }

                // Word-boundary-like match for single words.
                string needle = " " + term + " ";
                if (padded.Contains(needle)) return true;

                // Also catch simple punctuation separators, e.g., "dead,children".
                if (lower.Contains(term + ",") || lower.Contains(term + ".") || lower.Contains(term + "!") || lower.Contains(term + "?"))
                    return true;
            }

            return false;
        }

        private static bool HasLetters(string s)
        {
            foreach (char c in s)
                if (char.IsLetter(c))
                    return true;
            return false;
        }

        /// <summary>Called by the "AI Info" button.</summary>
        public void OnAiInfoPressed()
        {
            if (_aiPreviewPanel == null)
                EnsureAiInfoPanelExists();
            if (_aiPreviewPanel != null)
                _aiPreviewPanel.SetActive(!_aiPreviewPanel.activeSelf);
        }

        private void EnsureAiInfoButtonExists()
        {
            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            // Don't duplicate if already in scene.
            if (canvas.transform.Find("AiInfoButton") != null) return;

            var go = new GameObject("AiInfoButton",
                typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(canvas.transform, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot     = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(24f, 24f);
            rect.sizeDelta = new Vector2(200f, 56f);

            go.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f, 0.92f);

            var btn = go.GetComponent<Button>();
            btn.onClick.AddListener(OnAiInfoPressed);

            var txtGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(go.transform, false);
            var txtRect = txtGo.GetComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;

            var tmp = txtGo.GetComponent<TextMeshProUGUI>();
            tmp.text      = "? AI Info";
            tmp.fontSize  = 28f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = Color.white;
        }

        private void EnsureAiInfoPanelExists()
        {
            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            var root = new GameObject("AiInfoPanel",
                typeof(RectTransform), typeof(Image));
            root.transform.SetParent(canvas.transform, false);
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.08f, 0.06f);
            rootRect.anchorMax = new Vector2(0.92f, 0.94f);
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            root.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.88f);
            _aiPreviewPanel = root;

            const string info =
                "AI GENERATION PIPELINE\n\n" +
                "1.  OLLAMA  (Language Model — runs locally)\n" +
                "    \u2022 Generates the story: title, intro, objective, level descriptions, boss lore\n" +
                "    \u2022 Generates visual prompts for each asset type based on your theme\n" +
                "    \u2022 Generates gameplay stats: enemy speed, damage, patrol range\n\n" +
                "2.  STABLE DIFFUSION  (Image Generator — via A1111 API)\n" +
                "    \u2022 Background\n" +
                "    \u2022 Terrain tile\n" +
                "    \u2022 Player character\n" +
                "    \u2022 Ground enemy   |   Flying enemy   |   Shooting enemy\n" +
                "    \u2022 Projectile  \u2022  Pickup\n" +
                "    \u2022 Boss (Level 5 only)\n\n" +
                "    = ~9 textures per level  x  5 levels  =  ~45 images total\n\n" +
                "OPTIMISATION\n" +
                "    While you play a level, the NEXT level\u2019s textures are pre-generated\n" +
                "    in the background \u2014 so there\u2019s no waiting between levels.\n\n" +
                "Click anywhere to close";

            var txtGo = new GameObject("InfoText", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(root.transform, false);
            var txtRect = txtGo.GetComponent<RectTransform>();
            txtRect.anchorMin = new Vector2(0.05f, 0.05f);
            txtRect.anchorMax = new Vector2(0.95f, 0.95f);
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;

            var tmp = txtGo.GetComponent<TextMeshProUGUI>();
            tmp.text               = info;
            tmp.fontSize           = 26f;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode       = TextOverflowModes.Truncate;
            tmp.alignment          = TextAlignmentOptions.TopLeft;
            tmp.color              = Color.white;
            tmp.lineSpacing        = 4f;

            // Click-anywhere-to-close
            var closeGo = new GameObject("CloseOverlay",
                typeof(RectTransform), typeof(Image), typeof(Button));
            closeGo.transform.SetParent(root.transform, false);
            var closeRect = closeGo.GetComponent<RectTransform>();
            closeRect.anchorMin = Vector2.zero;
            closeRect.anchorMax = Vector2.one;
            closeRect.offsetMin = Vector2.zero;
            closeRect.offsetMax = Vector2.zero;
            closeGo.GetComponent<Image>().color = Color.clear; // invisible
            closeGo.GetComponent<Button>().onClick.AddListener(
                () => root.SetActive(false));
            // Put behind the text so text is still readable but the whole panel is clickable.
            closeGo.transform.SetAsFirstSibling();

            root.SetActive(false);
        }
    }
}