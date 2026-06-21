using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using Level;
using Player;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Game
{
    /// <summary>
    /// Central controller for the Game scene.
    /// </summary>
    public class GameController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("AiLevelPipeline that generates textures. Must be on a GameObject in this scene.")]
        public AiLevelPipeline pipeline;

        [Tooltip("LevelAssembler that applies textures to the tilemap. Must be on a GameObject in this scene.")]
        public LevelAssembler assembler;

        [Tooltip("LevelGenerator that builds the procedural geometry before AI textures arrive.")]
        public LevelGenerator levelGenerator;

        [Header("Ollama (optional — richer variety for every level)")]
        [Tooltip("Assign an OllamaClient so each level gets a uniquely generated plan while you play.\n" +
                 "Auto-found in the scene if not assigned. Leave null to use FallbackPlan only.")]
        public OllamaClient ollamaClient;

        [Header("Loading UI")]
        [Tooltip("Root GameObject of the loading / transition panel.")]
        public GameObject loadingPanel;

        [Tooltip("Text label inside the loading panel.")]
        public TMPro.TMP_Text loadingText;

        [Tooltip("Optional progress bar for generation status. Auto-created if null.")]
        public Slider loadingProgressBar;

        [Tooltip("Optional text near the progress bar.")]
        public TMP_Text loadingProgressText;

        [Header("Boss UI")]
        [Tooltip("Optional boss HP bar (created automatically if null).")]
        public Slider bossHealthBar;

        [Tooltip("Optional boss HP label.")]
        public TMP_Text bossHealthLabel;

        [Header("Settings")]
        [Tooltip("Seconds to wait before respawning after player death.")]
        [Min(0f)] public float respawnDelay = 1.2f;

        [Tooltip("Max seconds to wait for generation before giving up and showing the level with placeholders.")]
        [Min(10f)] public float generationTimeout = 40f;

        [Tooltip("If player Y goes below this threshold, it's treated as a fall death.")]
        public float fallDeathY = -10f;

        [Tooltip("Short invulnerability after respawn so the player is not hit instantly.")]
        [Min(0f)] public float respawnInvincibilitySeconds = 1f;

        private bool _levelActive;
        private bool _generationDone;
        private bool _respawning;

        private LevelLayout _layout;
        private Transform _playerTransform;
        private PlayerHealth _playerHealth;
        private bool _nextLevelPreGenStarted;
        private bool _loadingStyleApplied;
        private bool _startInputQueued;
        private bool _bossDefeated;
        private bool _transitioningToNextLevel;

        // ── Polish features
        private bool        _paused;
        private GameObject  _pausePanel;
        private Slider      _levelProgressBar;
        private int         _prevHudHealth = 99;

        private void Awake()
        {
            if (pipeline == null) pipeline = FindFirstObjectByType<AiLevelPipeline>();
            if (assembler == null) assembler = FindFirstObjectByType<LevelAssembler>();
            if (levelGenerator == null) levelGenerator = FindFirstObjectByType<LevelGenerator>();
            if (ollamaClient == null) ollamaClient = FindFirstObjectByType<OllamaClient>();

            if (pipeline == null) Debug.LogError("[GameController] AiLevelPipeline not found!");
            if (assembler == null) Debug.LogError("[GameController] LevelAssembler not found!");
            if (levelGenerator == null) Debug.LogWarning("[GameController] LevelGenerator not found.");

            // Zoom out camera if still at the old tight default (orthoSize 6).
            if (Camera.main != null && Camera.main.orthographic && Camera.main.orthographicSize < 8.5f)
                Camera.main.orthographicSize = 9f;

                // ── Load & prepare LevelPlan before generation ─────────────────────
                LevelPlan currentPlan = null;
                string planJson = PlayerPrefs.GetString(AiLevelPipeline.PlanPrefKey, "");
                if (!string.IsNullOrWhiteSpace(planJson))
                {
                    try { currentPlan = JsonUtility.FromJson<LevelPlan>(planJson); }
                    catch { currentPlan = null; }
                }
                if (currentPlan == null || !currentPlan.IsValid)
                    currentPlan = FallbackPlan.For(
                        GameSessionState.BaseTheme,
                        GameSessionState.CurrentLevelIndex,
                        GameSessionState.RunSeed);

                DifficultyMultiplier.Apply(currentPlan, GameSessionState.CurrentDifficulty);
                GameSessionState.SetCurrentLevelPlan(currentPlan);

                if (levelGenerator != null)
                    levelGenerator.enemyType = currentPlan.enemyType;

                _bossDefeated = GameSessionState.CurrentLevelIndex < 5;

            // Generate level in Awake so pickup/enemy positions are set before any Start() runs.
            if (levelGenerator != null)
            {
                DifficultyMultiplier.ApplyToGenerator(levelGenerator, GameSessionState.CurrentDifficulty);
                _layout = levelGenerator.Generate();
                assembler?.RebuildColliders();
            }

                ApplyLevelPlanStats(currentPlan);
        }

            private void ApplyLevelPlanStats(LevelPlan plan)
            {
                if (plan == null) return;
                foreach (var ec in FindObjectsByType<Gameplay.EnemyController>(FindObjectsSortMode.None))
                    if (ec.gameObject.activeSelf) ec.ApplyStats(plan.enemySpeed, plan.enemyPatrolRange, plan.enemyDamage);
                foreach (var fc in FindObjectsByType<Gameplay.FlyingEnemyController>(FindObjectsSortMode.None))
                    if (fc.gameObject.activeSelf) fc.ApplyStats(plan.enemySpeed, plan.enemyPatrolRange, plan.enemyDamage);
                foreach (var sc in FindObjectsByType<Gameplay.ShootingEnemyController>(FindObjectsSortMode.None))
                    if (sc.gameObject.activeSelf) sc.ApplyStats(plan.enemySpeed, plan.enemyPatrolRange, plan.enemyDamage);
                foreach (var bc in FindObjectsByType<Gameplay.BossController>(FindObjectsSortMode.None))
                    if (bc.gameObject.activeSelf)
                        bc.Configure(
                            GameSessionState.CurrentLore != null ? GameSessionState.CurrentLore.bossName : "Boss",
                            hp: 5,
                            speed: Mathf.Max(2.2f, plan.enemySpeed * 1.25f),
                            patrol: Mathf.Max(6f, plan.enemyPatrolRange * 1.7f),
                            damage: Mathf.Max(2, plan.enemyDamage + 1));
                foreach (var pc in FindObjectsByType<Gameplay.PickupController>(FindObjectsSortMode.None))
                    if (pc.gameObject.activeSelf) pc.ApplyStats(plan.pickupHealAmount, plan.pickupScoreValue);
            }

        private void Start()
        {
            _transitioningToNextLevel = false;
            GameSessionState.EnsureInitialized();
            PatchExistingHudPivots(); // fix old scenes created before pivot fix
            EnsureHudExists();
            EnsureBossUiExists();
            EnsureLoadingUiExtras();
            EnsurePauseUiExists();
            EnsureLevelProgressBarExists();

            AiLevelPipeline.OnBundleReady += OnBundleReady;
            AiLevelPipeline.OnGenerationFailed += OnGenerationFailed;
            AiLevelPipeline.OnGenerationProgress += OnGenerationProgress;
            LevelExit.PlayerReachedExit += OnExitReached;
            Gameplay.BossController.BossSpawned += OnBossSpawned;
            Gameplay.BossController.BossHealthChanged += OnBossHealthChanged;
            Gameplay.BossController.BossDefeated += OnBossDefeated;

            _playerHealth = FindFirstObjectByType<PlayerHealth>();
            if (_playerHealth != null)
            {
                _playerHealth.PlayerDied   += OnPlayerDied;
                _playerHealth.HealthChanged += OnPlayerHealthChanged;
                _prevHudHealth = _playerHealth.CurrentHealth;
            }

            var player = GameObject.FindWithTag("Player");
            if (player != null) _playerTransform = player.transform;

            if (GameSessionState.CurrentLevelIndex < 5)
                SetBossUiVisible(false);
            else
                SyncBossUiFromScene();

            string loadingMsg = "Generating level textures...";
            var lore = GameSessionState.CurrentLore;
            if (lore != null && !string.IsNullOrWhiteSpace(lore.title))
            {
                string levelFlavor = lore.GetLevelFlavor(GameSessionState.CurrentLevelIndex);
                string bossHint = GameSessionState.CurrentLevelIndex >= 5 && !string.IsNullOrWhiteSpace(lore.bossName)
                    ? $"\nBoss: {lore.bossName} - {lore.bossDesc}"
                    : "";
                loadingMsg =
                    $"{lore.title}\n" +
                    $"{lore.intro}\n\n" +
                    $"Objective: {lore.goal}\n" +
                    $"Level {GameSessionState.CurrentLevelIndex}: {levelFlavor}" +
                    bossHint +
                    "\n\nGenerating level textures...";
            }
            SetLoading(true, loadingMsg);
            SetGameplayFrozen(true);

            if (GameSessionState.TryConsumePendingBundle(GameSessionState.CurrentLevelIndex, out var pendingBundle))
            {
                Debug.Log($"[GameController] Applying pre-generated bundle for level {GameSessionState.CurrentLevelIndex}.");
                assembler?.ApplyBundle(pendingBundle);
                _generationDone = true;
                SetProgress(1f, "Generation complete.");
                StartCoroutine(ShowLevelBannerThenStart());
                return;
            }

            if (pipeline != null)
            {
                _startInputQueued = false;
                SetProgress(0f, "Preparing generation...");
                pipeline.Generate();
                StartCoroutine(GenerationWatchdog());
            }
            else
            {
                SetLoading(false, "");
                SetGameplayFrozen(false);
                _levelActive = true;
                _generationDone = true;
            }
        }

        private void Update()
        {
            // Pause toggle (Escape) — allowed at any time while level is active or already paused.
            if (Input.GetKeyDown(KeyCode.Escape) && !_respawning && _generationDone)
                TryTogglePause();

            if (!_levelActive && IsStartInputPressed())
                _startInputQueued = true;

            // Level progress bar
            if (_levelActive && _playerTransform != null && _levelProgressBar != null && _layout != null)
            {
                float range = _layout.exitPosition.x - _layout.playerSpawn.x;
                if (range > 0.1f)
                    _levelProgressBar.value = Mathf.Clamp01(
                        (_playerTransform.position.x - _layout.playerSpawn.x) / range);
            }

            if (!_levelActive || _respawning || _playerTransform == null) return;
            if (_playerTransform.position.y >= fallDeathY) return;

            HandleFallOutOfMap();
        }

        private void TryTogglePause()
        {
            if (!_paused && !_levelActive) return; // can't enter pause during loading
            _paused = !_paused;
            Time.timeScale = _paused ? 0f : 1f;
            if (_pausePanel != null) _pausePanel.SetActive(_paused);
        }

        private void OnPlayerHealthChanged(int newHealth)
        {
            if (newHealth < _prevHudHealth)
                StartCoroutine(ShakeCameraCoroutine());
            _prevHudHealth = newHealth;
        }

        private IEnumerator ShakeCameraCoroutine(float duration = 0.18f, float magnitude = 0.10f)
        {
            if (Camera.main == null) yield break;
            var cam      = Camera.main.transform;
            Vector3 orig = cam.localPosition;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t  = 1f - (elapsed / duration);
                cam.localPosition = orig + new Vector3(
                    Random.Range(-1f, 1f) * magnitude * t,
                    Random.Range(-1f, 1f) * magnitude * t, 0f);
                elapsed += Time.unscaledDeltaTime; // unscaled so shake works during slow-mo
                yield return null;
            }
            cam.localPosition = orig;
        }

        private void OnDestroy()
        {
            Time.timeScale = 1f; // safety reset if destroyed while paused
            AiLevelPipeline.OnBundleReady -= OnBundleReady;
            AiLevelPipeline.OnGenerationFailed -= OnGenerationFailed;
            AiLevelPipeline.OnGenerationProgress -= OnGenerationProgress;
            LevelExit.PlayerReachedExit -= OnExitReached;
            Gameplay.BossController.BossSpawned -= OnBossSpawned;
            Gameplay.BossController.BossHealthChanged -= OnBossHealthChanged;
            Gameplay.BossController.BossDefeated -= OnBossDefeated;

            if (_playerHealth != null)
            {
                _playerHealth.PlayerDied    -= OnPlayerDied;
                _playerHealth.HealthChanged -= OnPlayerHealthChanged;
            }
        }

        private void OnBundleReady(LevelBundle bundle)
        {
            if (_generationDone)
            {
                Debug.LogWarning("[GameController] OnBundleReady called but generation already marked done — ignoring duplicate.");
                return;
            }
            Debug.Log("[GameController] Bundle received, applying to scene.");
            assembler?.ApplyBundle(bundle);
            _generationDone = true;
            SetProgress(1f, "Generation complete.");
            StartCoroutine(ShowLevelBannerThenStart());
        }

        private void OnGenerationProgress(float value, string stage)
        {
            SetProgress(value, stage);
        }

        private void OnBossSpawned(string bossName, int currentHp, int maxHp)
        {
            if (GameSessionState.CurrentLevelIndex < 5) return;
            _bossDefeated = false;
            SetBossUiVisible(true);
            SetBossHealth(currentHp, maxHp, bossName);
        }

        private void OnBossHealthChanged(int currentHp, int maxHp)
        {
            if (GameSessionState.CurrentLevelIndex < 5) return;
            SetBossHealth(currentHp, maxHp, null);
        }

        private void OnBossDefeated()
        {
            if (GameSessionState.CurrentLevelIndex < 5) return;
            _bossDefeated = true;
            SetBossUiVisible(false);
            if (!_levelActive) return;

            _levelActive = false;
            ShowWinScreen();
        }

        private IEnumerator ShowLevelBannerThenStart()
        {
            var lore = GameSessionState.CurrentLore;
            int lvl  = GameSessionState.CurrentLevelIndex;

            string bannerMsg;

            if (lvl >= 5 && lore != null && !string.IsNullOrWhiteSpace(lore.bossName))
            {
                bannerMsg =
                    $"- BOSS FIGHT -\n\n{lore.bossName}\n\n{lore.bossDesc}\n\nPress any key to begin";
            }
            else
            {
                string flavor = lore != null ? lore.GetLevelFlavor(lvl) : "";
                bannerMsg = string.IsNullOrWhiteSpace(flavor)
                    ? $"Level {lvl} / 5\n\nPress any key to start"
                    : $"Level {lvl} / 5\n\n{flavor}\n\nPress any key to start";
            }

            SetLoading(true, bannerMsg);
            if (!_startInputQueued)
                yield return new WaitUntil(() => _startInputQueued);
            _startInputQueued = false;

            SetLoading(false, "");
            SetGameplayFrozen(false);
            _levelActive = true;
            StartNextLevelPreGeneration();
        }

        private static bool IsStartInputPressed()
        {
            bool legacyInput = Input.anyKeyDown
                || Input.GetMouseButtonDown(0)
                || Input.GetMouseButtonDown(1)
                || Input.GetKeyDown(KeyCode.Return)
                || Input.GetKeyDown(KeyCode.Space)
                || Input.touchCount > 0;

#if ENABLE_INPUT_SYSTEM
            bool newInput = false;
            if (Keyboard.current != null)
                newInput |= Keyboard.current.anyKey.wasPressedThisFrame;
            if (Mouse.current != null)
                newInput |= Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame;
            if (Touchscreen.current != null)
                newInput |= Touchscreen.current.primaryTouch.press.wasPressedThisFrame;

            return legacyInput || newInput;
#else
            return legacyInput;
#endif
        }

        private void OnGenerationFailed(string error)
        {
            Debug.LogError($"[GameController] Generation failed: {error}");
            SetProgress(1f, "Generation failed. Starting placeholders...");
            SetLoading(false, "");
            SetGameplayFrozen(false);
            _levelActive = true;
            _generationDone = true;
            StartNextLevelPreGeneration();
        }

        private void OnExitReached()
        {
            if (!_levelActive || _transitioningToNextLevel) return;
            _levelActive = false;
            
            // Check if this was the final boss level (level 5)
            if (GameSessionState.CurrentLevelIndex >= 5)
            {
                if (!_bossDefeated)
                {
                    Debug.Log("[GameController] Exit blocked: boss still alive.");
                    _levelActive = true;
                    StartCoroutine(ShowBossLockedHint());
                    return;
                }

                Debug.Log("[GameController] Boss defeated! Game complete!");
                ShowWinScreen();
                return;
            }
            
            PrepareNextLevelInputs();
            StartCoroutine(TransitionToNextLevelCoroutine());
        }

        private IEnumerator TransitionToNextLevelCoroutine()
        {
            _transitioningToNextLevel = true;
            _paused = false;
            Time.timeScale = 1f;
            int targetLevel = GameSessionState.CurrentLevelIndex;

            SetGameplayFrozen(true);
            SetLoading(true, $"Preparing level {targetLevel}...\n\nFinishing texture generation.");

            float waitElapsed = 0f;
            float waitTimeout = Mathf.Max(8f, GetEffectiveGenerationTimeout());

            while (waitElapsed < waitTimeout)
            {
                bool hasPendingForTarget =
                    GameSessionState.PendingLevelBundle != null &&
                    GameSessionState.PendingLevelIndex == targetLevel;

                if (hasPendingForTarget)
                {
                    Debug.Log($"[GameController] Next level {targetLevel} bundle ready before scene reload.");
                    break;
                }

                // If no generation is running, waiting longer will not help.
                if (!AiLevelPipeline.IsGenerating)
                    break;

                waitElapsed += Time.deltaTime;
                yield return null;
            }

            if (GameSessionState.PendingLevelBundle == null || GameSessionState.PendingLevelIndex != targetLevel)
                Debug.LogWarning($"[GameController] No completed pre-gen bundle for level {targetLevel}. Next scene will generate textures.");

            Debug.Log("[GameController] Exit reached, loading next level scene.");
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }

        private void ShowWinScreen()
        {
            int finalScore = GameSessionState.Score;
            SaveHighScore(finalScore);
            int highScore  = PlayerPrefs.GetInt("HighScore", 0);

            var lore = GameSessionState.CurrentLore;
            string title    = lore != null && !string.IsNullOrWhiteSpace(lore.title) ? lore.title : "Victory";
            string goalLine = lore != null && !string.IsNullOrWhiteSpace(lore.goal)
                ? $"\"{ lore.goal }\"\n\n"
                : "";
            string bossLine = lore != null && !string.IsNullOrWhiteSpace(lore.bossName)
                ? $"{lore.bossName} has been defeated!\n\n"
                : "The boss has been defeated!\n\n";
            string hsLine   = finalScore >= highScore ? "\n\nNew High Score!" : $"\n\nHigh Score: {highScore}";

            string msg = $"VICTORY!\n\n{title}\n\n{bossLine}{goalLine}Final Score: {finalScore}{hsLine}";

            if (loadingProgressBar != null) loadingProgressBar.gameObject.SetActive(false);
            if (loadingProgressText != null) loadingProgressText.gameObject.SetActive(false);

            SetLoading(true, msg);
            CreateEndScreenButtons(isWin: true);
        }

        private void ShowGameOverScreen()
        {
            int finalScore = GameSessionState.Score;
            SaveHighScore(finalScore);
            int highScore  = PlayerPrefs.GetInt("HighScore", 0);

            var lore = GameSessionState.CurrentLore;
            string loreLine = lore != null && !string.IsNullOrWhiteSpace(lore.title)
                ? $"\"{ lore.title }\"\n\n"
                : "";
            string hsLine = $"\n\nHigh Score: {highScore}";

            string msg = $"GAME OVER\n\n{loreLine}Final Score: {finalScore}\n\nYou were defeated...{hsLine}";

            if (loadingProgressBar != null) loadingProgressBar.gameObject.SetActive(false);
            if (loadingProgressText != null) loadingProgressText.gameObject.SetActive(false);

            SetLoading(true, msg);
            CreateEndScreenButtons(isWin: false);
        }

        private static void SaveHighScore(int score)
        {
            if (score > PlayerPrefs.GetInt("HighScore", 0))
            {
                PlayerPrefs.SetInt("HighScore", score);
                PlayerPrefs.Save();
            }
        }

        private void CreateEndScreenButtons(bool isWin)
        {
            if (loadingPanel == null) return;

            var existing = loadingPanel.transform.Find("EndScreenButtons");
            if (existing != null) Destroy(existing.gameObject);

            var root = new GameObject("EndScreenButtons", typeof(RectTransform));
            root.transform.SetParent(loadingPanel.transform, false);
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.20f, 0.04f);
            rootRect.anchorMax = new Vector2(0.80f, 0.16f);
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            string leftLabel = isWin ? "Play Again" : "Retry";
            var leftBtn = CreateOverlayButton(root.transform, "LeftBtn",
                new Vector2(0f, 0f), new Vector2(0.46f, 1f), leftLabel);
            leftBtn.onClick.AddListener(() =>
            {
                GameSessionState.ResetRun();
                SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            });

            var rightBtn = CreateOverlayButton(root.transform, "RightBtn",
                new Vector2(0.54f, 0f), new Vector2(1f, 1f), "Main Menu");
            rightBtn.onClick.AddListener(() =>
            {
                GameSessionState.ResetRun();
                SceneManager.LoadScene("MainMenu");
            });
        }

        private static UnityEngine.UI.Button CreateOverlayButton(
            Transform parent, string objName,
            Vector2 anchorMin, Vector2 anchorMax, string label)
        {
            var go = new GameObject(objName,
                typeof(RectTransform), typeof(Image), typeof(UnityEngine.UI.Button));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = new Vector2(6f, 0f);
            rect.offsetMax = new Vector2(-6f, 0f);

            var img = go.GetComponent<Image>();
            img.color = new Color(0.18f, 0.18f, 0.24f, 0.95f);

            var btn = go.GetComponent<UnityEngine.UI.Button>();
            var colors = btn.colors;
            colors.normalColor    = Color.white;
            colors.highlightedColor = new Color(0.75f, 0.75f, 1.00f, 1f);
            colors.pressedColor   = new Color(0.45f, 0.45f, 0.65f, 1f);
            btn.colors = colors;

            var txtGo = new GameObject("Text",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(go.transform, false);
            var txtRect = txtGo.GetComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;

            var tmp = txtGo.GetComponent<TextMeshProUGUI>();
            tmp.text      = label;
            tmp.fontSize  = 34f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = Color.white;

            return btn;
        }

        private void PrepareNextLevelInputs()
        {
            GameSessionState.AdvanceLevel();

            string baseTheme = GameSessionState.BaseTheme;
            string variant = ThemeVariationComposer.ComposeVariantTag(
                baseTheme,
                GameSessionState.CurrentLevelIndex,
                GameSessionState.RunSeed);
            string effectiveTheme = $"{baseTheme}, {variant}";

            var nextPlan = FallbackPlan.For(baseTheme, GameSessionState.CurrentLevelIndex, GameSessionState.RunSeed);

            PlayerPrefs.SetString(AiLevelPipeline.ThemePrefKey, effectiveTheme);
            PlayerPrefs.SetString(AiLevelPipeline.PlanPrefKey, JsonUtility.ToJson(nextPlan));
            PlayerPrefs.Save();

            Debug.Log($"[GameController] Prepared level {GameSessionState.CurrentLevelIndex} inputs for theme '{baseTheme}'.");
        }

        private void StartNextLevelPreGeneration()
        {
            if (_nextLevelPreGenStarted) return;
            if (pipeline == null) return;
            if (GameSessionState.CurrentLevelIndex >= 5) return;

            _nextLevelPreGenStarted = true;
            int targetLevel = Mathf.Max(1, GameSessionState.CurrentLevelIndex + 1);
            if (targetLevel > 5)
            {
                _nextLevelPreGenStarted = false;
                return;
            }
            string baseTheme = string.IsNullOrWhiteSpace(GameSessionState.BaseTheme) ? "forest" : GameSessionState.BaseTheme;
            StartCoroutine(PreGenCoroutine(targetLevel, baseTheme));
        }

        /// <summary>
        /// Background coroutine that optionally calls Ollama for a unique next-level plan,
        /// then feeds that plan to the AI pipeline so every level has tailored prompts.
        /// </summary>
        private IEnumerator PreGenCoroutine(int targetLevel, string baseTheme)
        {
            LevelPlan plan = null;

            // Ask Ollama for a theme-specific plan while the player is still playing.
            if (ollamaClient != null)
            {
                string variantTag     = ThemeVariationComposer.ComposeVariantTag(baseTheme, targetLevel, GameSessionState.RunSeed);
                string effectiveTheme = $"{baseTheme}, {variantTag}";
                Debug.Log($"[GameController] Pre-gen: asking Ollama for level {targetLevel} plan (theme: \"{effectiveTheme}\").");

                yield return ollamaClient.GeneratePlan(
                    effectiveTheme,
                    p => plan = p,
                    e => Debug.LogWarning($"[GameController] Ollama pre-gen level {targetLevel} failed: {e}. Using FallbackPlan."));
            }

            // Fall back to template-based prompts if Ollama is unavailable or returned an invalid plan.
            if (plan == null || !plan.IsValid)
                plan = FallbackPlan.For(baseTheme, targetLevel, GameSessionState.RunSeed);

            if (pipeline == null)
            {
                _nextLevelPreGenStarted = false;
                yield break;
            }

            bool started = pipeline.GenerateFromPlan(
                plan,
                bundle =>
                {
                    _nextLevelPreGenStarted = false;
                    GameSessionState.StorePendingBundle(targetLevel, bundle);
                    Debug.Log($"[GameController] Pre-generation finished for level {targetLevel}.");
                },
                error =>
                {
                    _nextLevelPreGenStarted = false;
                    Debug.LogWarning($"[GameController] Pre-generation failed for level {targetLevel}: {error}");
                });

            if (!started)
            {
                _nextLevelPreGenStarted = false;
                Debug.LogWarning($"[GameController] Pre-generation skipped for level {targetLevel} (pipeline busy).");
            }
        }

        private void OnPlayerDied()
        {
            if (GameSessionState.Lives > 0) return;

            _levelActive = false;
            Debug.Log("[GameController] Player out of lives — showing Game Over screen.");
            ShowGameOverScreen();
        }

        private void HandleFallOutOfMap()
        {
            if (_playerHealth == null)
                _playerHealth = FindFirstObjectByType<PlayerHealth>();

            if (_playerHealth != null)
                _playerHealth.TakeDamage(1, ignoreInvincibility: true);

            // Use CurrentHealth (not GameSessionState.Lives) because OnPlayerDied() calls
            // ResetRun() which sets Lives back to MaxLives before control returns here.
            // If CurrentHealth is 0 the PlayerHealth.PlayerDied event already fired and
            // GameController.OnPlayerDied() has handled the death (reset + reload).
            if (_playerHealth != null && _playerHealth.CurrentHealth > 0)
                StartCoroutine(RespawnToStartCoroutine());
        }

        private IEnumerator RespawnToStartCoroutine()
        {
            if (_respawning) yield break;
            _respawning = true;

            // Teleport the player to spawn immediately so they are not frozen
            // at the fall position (which looked like a 3-second lock-up).
            Vector2 spawn = _layout != null ? _layout.playerSpawn : new Vector2(1.5f, 2f);

            if (_playerTransform == null)
            {
                var player = GameObject.FindWithTag("Player");
                if (player != null) _playerTransform = player.transform;
            }

            if (_playerTransform != null)
            {
                _playerTransform.position = new Vector3(spawn.x, spawn.y, _playerTransform.position.z);
                var rb = _playerTransform.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.linearVelocity = Vector2.zero;
                    rb.angularVelocity = 0f;
                }
            }

            // Brief freeze AFTER teleport — just long enough to feel intentional.
            SetGameplayFrozen(true);
            yield return new WaitForSeconds(Mathf.Min(respawnDelay, 0.4f));
            SetGameplayFrozen(false);

            _playerHealth?.SetInvincibleFor(respawnInvincibilitySeconds);
            _respawning = false;
        }

        private float GetEffectiveGenerationTimeout()
        {
            float timeout = Mathf.Max(10f, generationTimeout);

            // Rembg postprocessing adds per-sprite HTTP calls. Give generation
            // more headroom so we do not force placeholder visuals too early.
            var rembg = FindFirstObjectByType<RembgClient>();
            if (rembg != null && rembg.IsEnabled)
                timeout = Mathf.Max(timeout, 90f);

            return timeout;
        }

        private IEnumerator GenerationWatchdog()
        {
            float timeout = GetEffectiveGenerationTimeout();
            yield return new WaitForSeconds(timeout);
            if (!_generationDone)
            {
                Debug.LogWarning($"[GameController] Generation timed out after {timeout:F1}s, forcing level start with placeholder visuals.");
                if (pipeline != null)
                    pipeline.CancelActiveGeneration();
                else
                    AiLevelPipeline.IsGeneratingPublicReset();
                SetLoading(false, "");
                SetGameplayFrozen(false);
                _levelActive = true;
                _generationDone = true;
            }
        }

        private void SetLoading(bool active, string msg)
        {
            if (loadingPanel != null) loadingPanel.SetActive(active);
            if (active) EnsureReadableLoadingOverlayStyle();
            if (loadingText != null) loadingText.text = msg;
            if (loadingProgressBar != null) loadingProgressBar.gameObject.SetActive(active);
            if (loadingProgressText != null) loadingProgressText.gameObject.SetActive(active);
        }

        private void SetProgress(float value, string msg)
        {
            if (loadingProgressBar != null)
                loadingProgressBar.value = Mathf.Clamp01(value);

            if (loadingProgressText != null)
            {
                int pct = Mathf.RoundToInt(Mathf.Clamp01(value) * 100f);
                loadingProgressText.text = string.IsNullOrWhiteSpace(msg)
                    ? $"{pct}%"
                    : $"{pct}% - {msg}";
            }
        }

        private void EnsureLoadingUiExtras()
        {
            if (loadingPanel == null) return;
            var panelRect = loadingPanel.GetComponent<RectTransform>();
            if (panelRect == null) return;

            if (loadingProgressBar == null)
            {
                var sliderGo = new GameObject("LoadingProgressBar", typeof(RectTransform), typeof(Slider));
                sliderGo.transform.SetParent(loadingPanel.transform, false);

                var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
                bgGo.transform.SetParent(sliderGo.transform, false);
                var fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
                fillAreaGo.transform.SetParent(sliderGo.transform, false);
                var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
                fillGo.transform.SetParent(fillAreaGo.transform, false);

                var bgRect = bgGo.GetComponent<RectTransform>();
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.offsetMin = Vector2.zero;
                bgRect.offsetMax = Vector2.zero;

                var fillAreaRect = fillAreaGo.GetComponent<RectTransform>();
                fillAreaRect.anchorMin = Vector2.zero;
                fillAreaRect.anchorMax = Vector2.one;
                fillAreaRect.offsetMin = new Vector2(4f, 4f);
                fillAreaRect.offsetMax = new Vector2(-4f, -4f);

                var fillRect = fillGo.GetComponent<RectTransform>();
                fillRect.anchorMin = Vector2.zero;
                fillRect.anchorMax = Vector2.one;
                fillRect.offsetMin = Vector2.zero;
                fillRect.offsetMax = Vector2.zero;

                var bgImg = bgGo.GetComponent<Image>();
                bgImg.color = new Color(1f, 1f, 1f, 0.20f);
                var fillImg = fillGo.GetComponent<Image>();
                fillImg.color = new Color(0.22f, 0.88f, 0.62f, 0.95f);

                var slider = sliderGo.GetComponent<Slider>();
                slider.fillRect = fillRect;
                slider.targetGraphic = fillImg;
                slider.direction = Slider.Direction.LeftToRight;
                slider.minValue = 0f;
                slider.maxValue = 1f;
                slider.value = 0f;
                loadingProgressBar = slider;
            }

            var sliderRect = loadingProgressBar.GetComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0.18f, 0.08f);
            sliderRect.anchorMax = new Vector2(0.82f, 0.12f);
            sliderRect.offsetMin = Vector2.zero;
            sliderRect.offsetMax = Vector2.zero;

            if (loadingProgressText == null)
            {
                var txtGo = new GameObject("LoadingProgressText", typeof(RectTransform), typeof(TextMeshProUGUI));
                txtGo.transform.SetParent(loadingPanel.transform, false);
                loadingProgressText = txtGo.GetComponent<TextMeshProUGUI>();
            }

            var txtRect = loadingProgressText.GetComponent<RectTransform>();
            txtRect.anchorMin = new Vector2(0.18f, 0.12f);
            txtRect.anchorMax = new Vector2(0.82f, 0.17f);
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;

            loadingProgressText.alignment = TextAlignmentOptions.Center;
            loadingProgressText.enableAutoSizing = false;
            loadingProgressText.fontSize = 28f;
            loadingProgressText.color = new Color(0.95f, 0.95f, 0.95f, 1f);
            SetProgress(0f, "Preparing generation...");
        }

        private void EnsureReadableLoadingOverlayStyle()
        {
            if (_loadingStyleApplied) return;

            if (loadingPanel != null)
            {
                var panelRect = loadingPanel.GetComponent<RectTransform>();
                if (panelRect != null)
                {
                    panelRect.anchorMin = Vector2.zero;
                    panelRect.anchorMax = Vector2.one;
                    panelRect.offsetMin = Vector2.zero;
                    panelRect.offsetMax = Vector2.zero;
                }

                var panelImage = loadingPanel.GetComponent<Image>();
                if (panelImage != null)
                    panelImage.color = new Color(0f, 0f, 0f, 0.82f);
            }

            if (loadingText != null)
            {
                var textRect = loadingText.GetComponent<RectTransform>();
                if (textRect != null)
                {
                    textRect.anchorMin = new Vector2(0.12f, 0.16f);
                    textRect.anchorMax = new Vector2(0.88f, 0.84f);
                    textRect.offsetMin = Vector2.zero;
                    textRect.offsetMax = Vector2.zero;
                }

                loadingText.alignment = TextAlignmentOptions.Center;
                loadingText.enableAutoSizing = false;
                loadingText.textWrappingMode = TextWrappingModes.Normal;
                loadingText.overflowMode = TextOverflowModes.Truncate;
                loadingText.fontSize = 46f;
                loadingText.lineSpacing = 8f;
                loadingText.color = Color.white;
            }

            _loadingStyleApplied = true;
        }

        private void SetGameplayFrozen(bool frozen)
        {
            var playerMovement = FindFirstObjectByType<PlayerMovement2D>();
            if (playerMovement != null) playerMovement.enabled = !frozen;

            var rigidbodies = FindObjectsByType<Rigidbody2D>(FindObjectsSortMode.None);
            foreach (var rb in rigidbodies)
            {
                if (rb == null || rb.bodyType != RigidbodyType2D.Dynamic) continue;
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.simulated = !frozen;
            }
        }

        /// <summary>
        /// Patches scenes built before the HUD pivot fix: sets pivot=(0,1) on HudPanel
        /// and its TMP children so the panel appears in the top-left corner correctly.
        /// </summary>
        private static void PatchExistingHudPivots()
        {
            var hudPanelGo = GameObject.Find("HudPanel");
            if (hudPanelGo == null) return;

            var rt = hudPanelGo.GetComponent<RectTransform>();
            if (rt != null && rt.pivot != new Vector2(0f, 1f))
                rt.pivot = new Vector2(0f, 1f);

            foreach (RectTransform child in hudPanelGo.transform)
            {
                if (child.pivot != new Vector2(0f, 1f))
                    child.pivot = new Vector2(0f, 1f);
            }
        }

        private void EnsureHudExists()
        {
            if (FindFirstObjectByType<UI.GameHudController>() != null) return;

            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                var canvasGo = new GameObject("GameUI_Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
            }

            var panelGo = new GameObject("HudPanel", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(canvas.transform, false);
            var panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(20f, -20f);
            panelRect.sizeDelta = new Vector2(340f, 178f);
            panelGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            var livesText = CreateHudText(panelGo.transform, "LivesText", new Vector2(14f, -12f), "Lives: 3");
            var scoreText = CreateHudText(panelGo.transform, "ScoreText", new Vector2(14f, -64f), "Score: 0");
            var levelText = CreateHudText(panelGo.transform, "LevelText", new Vector2(14f, -112f), "Level: 1/5");

            var hud = panelGo.AddComponent<UI.GameHudController>();
            hud.BindTexts(scoreText, livesText, levelText);
        }

        private void EnsureBossUiExists()
        {
            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            if (bossHealthBar == null)
            {
                var root = new GameObject("BossHealthRoot", typeof(RectTransform));
                root.transform.SetParent(canvas.transform, false);

                var barGo = new GameObject("BossHealthBar", typeof(RectTransform), typeof(Slider));
                barGo.transform.SetParent(root.transform, false);

                var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
                bgGo.transform.SetParent(barGo.transform, false);
                var fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
                fillAreaGo.transform.SetParent(barGo.transform, false);
                var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
                fillGo.transform.SetParent(fillAreaGo.transform, false);

                var bgRect = bgGo.GetComponent<RectTransform>();
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.offsetMin = Vector2.zero;
                bgRect.offsetMax = Vector2.zero;

                var areaRect = fillAreaGo.GetComponent<RectTransform>();
                areaRect.anchorMin = Vector2.zero;
                areaRect.anchorMax = Vector2.one;
                areaRect.offsetMin = new Vector2(4f, 4f);
                areaRect.offsetMax = new Vector2(-4f, -4f);

                var fillRect = fillGo.GetComponent<RectTransform>();
                fillRect.anchorMin = Vector2.zero;
                fillRect.anchorMax = Vector2.one;
                fillRect.offsetMin = Vector2.zero;
                fillRect.offsetMax = Vector2.zero;

                var bg = bgGo.GetComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.65f);
                var fill = fillGo.GetComponent<Image>();
                fill.color = new Color(0.84f, 0.14f, 0.14f, 0.95f);

                var slider = barGo.GetComponent<Slider>();
                slider.fillRect = fillRect;
                slider.targetGraphic = fill;
                slider.direction = Slider.Direction.LeftToRight;
                slider.minValue = 0f;
                slider.maxValue = 1f;
                slider.value = 1f;
                bossHealthBar = slider;

                var labelGo = new GameObject("BossHealthLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
                labelGo.transform.SetParent(root.transform, false);
                bossHealthLabel = labelGo.GetComponent<TextMeshProUGUI>();

                var rootRect = root.GetComponent<RectTransform>();
                rootRect.anchorMin = new Vector2(0.22f, 0.92f);
                rootRect.anchorMax = new Vector2(0.78f, 0.995f);
                rootRect.offsetMin = Vector2.zero;
                rootRect.offsetMax = Vector2.zero;

                var barRect = bossHealthBar.GetComponent<RectTransform>();
                barRect.anchorMin = new Vector2(0f, 0f);
                barRect.anchorMax = new Vector2(1f, 0.62f);
                barRect.offsetMin = Vector2.zero;
                barRect.offsetMax = Vector2.zero;

                var labelRect = bossHealthLabel.GetComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0f, 0.62f);
                labelRect.anchorMax = new Vector2(1f, 1f);
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
            }

            if (bossHealthLabel != null)
            {
                bossHealthLabel.alignment = TextAlignmentOptions.Center;
                bossHealthLabel.enableAutoSizing = false;
                bossHealthLabel.fontSize = 26f;
                bossHealthLabel.color = Color.white;
            }

            SetBossUiVisible(false);
        }

        private void SyncBossUiFromScene()
        {
            var boss = FindFirstObjectByType<Gameplay.BossController>();
            if (boss == null || !boss.isActiveAndEnabled)
            {
                SetBossUiVisible(false);
                return;
            }

            string name = GameSessionState.CurrentLore != null && !string.IsNullOrWhiteSpace(GameSessionState.CurrentLore.bossName)
                ? GameSessionState.CurrentLore.bossName
                : "Boss";
            SetBossUiVisible(true);
            SetBossHealth(boss.CurrentHp, boss.MaxHp, name);
        }

        private void SetBossHealth(int current, int max, string bossName)
        {
            int m = Mathf.Max(1, max);
            int c = Mathf.Clamp(current, 0, m);

            if (bossHealthBar != null)
                bossHealthBar.value = (float)c / m;

            if (bossHealthLabel != null)
            {
                string title = string.IsNullOrWhiteSpace(bossName) && GameSessionState.CurrentLore != null
                    ? GameSessionState.CurrentLore.bossName
                    : bossName;
                if (string.IsNullOrWhiteSpace(title)) title = "Boss";
                bossHealthLabel.text = $"{title}  HP {c}/{m}";
            }
        }

        private void SetBossUiVisible(bool visible)
        {
            if (bossHealthBar != null) bossHealthBar.transform.parent.gameObject.SetActive(visible);
            if (bossHealthLabel != null) bossHealthLabel.gameObject.SetActive(visible);
        }

        private IEnumerator ShowBossLockedHint()
        {
            SetLoading(true, "Boss still lives!\n\nDefeat the boss first.\n\nPress any key to continue");
            yield return new WaitUntil(() => Input.anyKeyDown || Input.GetMouseButtonDown(0));
            SetLoading(false, "");
        }

        private static TextMeshProUGUI CreateHudText(Transform parent, string objectName, Vector2 anchoredPos, string text)
        {
            var go = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = new Vector2(-14f, 46f);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 42;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.color = Color.white;
            return tmp;
        }

        private void EnsurePauseUiExists()
        {
            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            var root = new GameObject("PausePanel",
                typeof(RectTransform), typeof(UnityEngine.UI.Image));
            root.transform.SetParent(canvas.transform, false);
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            root.GetComponent<UnityEngine.UI.Image>().color = new Color(0f, 0f, 0f, 0.72f);
            root.SetActive(false);
            _pausePanel = root;

            var titleGo = new GameObject("PauseTitle",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            titleGo.transform.SetParent(root.transform, false);
            var titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.3f, 0.60f);
            titleRect.anchorMax = new Vector2(0.7f, 0.74f);
            titleRect.offsetMin = Vector2.zero;
            titleRect.offsetMax = Vector2.zero;
            var titleTmp = titleGo.GetComponent<TextMeshProUGUI>();
            titleTmp.text      = "PAUSED";
            titleTmp.fontSize  = 60f;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.color     = Color.white;

            var resumeBtn = CreateOverlayButton(root.transform, "ResumeBtn",
                new Vector2(0.3f, 0.43f), new Vector2(0.7f, 0.56f), "Resume");
            resumeBtn.onClick.AddListener(() => TryTogglePause());

            var menuBtn = CreateOverlayButton(root.transform, "MenuBtn",
                new Vector2(0.3f, 0.28f), new Vector2(0.7f, 0.41f), "Main Menu");
            menuBtn.onClick.AddListener(() =>
            {
                Time.timeScale = 1f;
                GameSessionState.ResetRun();
                UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
            });
        }

        private void EnsureLevelProgressBarExists()
        {
            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            var sliderGo = new GameObject("LevelProgressBar",
                typeof(RectTransform), typeof(Slider));
            sliderGo.transform.SetParent(canvas.transform, false);

            var bgGo       = new GameObject("Background",  typeof(RectTransform), typeof(UnityEngine.UI.Image));
            var fillAreaGo = new GameObject("Fill Area",   typeof(RectTransform));
            var fillGo     = new GameObject("Fill",        typeof(RectTransform), typeof(UnityEngine.UI.Image));
            bgGo.transform.SetParent(sliderGo.transform, false);
            fillAreaGo.transform.SetParent(sliderGo.transform, false);
            fillGo.transform.SetParent(fillAreaGo.transform, false);

            void StretchRect(RectTransform rt)
            {
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            }
            StretchRect(bgGo.GetComponent<RectTransform>());
            StretchRect(fillAreaGo.GetComponent<RectTransform>());
            StretchRect(fillGo.GetComponent<RectTransform>());

            bgGo.GetComponent<UnityEngine.UI.Image>().color   = new Color(0f,    0f,    0f,    0.42f);
            fillGo.GetComponent<UnityEngine.UI.Image>().color = new Color(0.28f, 0.82f, 0.34f, 0.88f);

            var slider = sliderGo.GetComponent<Slider>();
            slider.fillRect      = fillGo.GetComponent<RectTransform>();
            slider.targetGraphic = fillGo.GetComponent<UnityEngine.UI.Image>();
            slider.direction     = Slider.Direction.LeftToRight;
            slider.minValue      = 0f;
            slider.maxValue      = 1f;
            slider.value         = 0f;
            slider.interactable  = false;
            _levelProgressBar    = slider;

            var sliderRect = sliderGo.GetComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, 0.988f);
            sliderRect.anchorMax = new Vector2(1f, 1f);
            sliderRect.offsetMin = Vector2.zero;
            sliderRect.offsetMax = Vector2.zero;
        }
    }
}
