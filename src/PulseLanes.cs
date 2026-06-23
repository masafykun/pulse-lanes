using System.Collections.Generic;
using UnityEngine;

// PULSE LANES — a 3D rhythm-tap arcade game.
// Glowing notes stream down a 4-lane neon highway toward a hit-line near the camera, locked to a
// 128-BPM conductor. Strike each note as it crosses the line: D/F/J/K (or 1-4, or tap the lane on
// touch). Timing is judged PERFECT / GOOD / MISS. A streak builds a COMBO -> score multiplier and,
// past a threshold, ignites FEVER (2x score + warm screen tint). The hit SFX climbs a pentatonic
// scale as your combo grows, so a clean run literally plays a rising melody. Missed notes drain a
// health charge; empty = GAME OVER -> tap / R to retry. Difficulty ramps forever (density + doubles).
//
// 100% code-generated for reliable WebGL with engine stripping disabled (coin-cruiser lesson):
//   * NO Rigidbody / NO colliders — notes are pure Transform-driven; hits are timing-window tests.
//   * Particles / SFX go through Juice (CreatePrimitive-based, strip-safe).
//   * Default scene camera/light are removed and rebuilt so we never double-light or shoot the
//     wrong camera (AutoShot reads Camera.main).
public class PulseLanes : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__PulseLanes");
        go.AddComponent<PulseLanes>();
        DontDestroyOnLoad(go);
    }

    // ---- conductor / chart ----
    const float BPM       = 128f;
    static float BeatDur  => 60f / BPM;            // 0.46875s
    const int   LANES     = 4;
    const float SPACING   = 2.0f;                  // lane center spacing
    const float NOTE_W    = 1.55f;
    const float NOTE_Y    = 0.42f;
    const float HIT_Z     = 0f;                    // hit line
    const float FAR_Z     = 58f;                   // notes spawn here
    const float PAST_Z    = -6f;
    const float TRAVEL    = 1.95f;                 // seconds from spawn -> hit line
    static float Speed    => (FAR_Z - HIT_Z) / TRAVEL;

    // timing windows (seconds)
    const float PERFECT_W = 0.060f;
    const float GOOD_W    = 0.135f;
    const float PASS_W    = 0.150f;                // un-tapped beyond this past the line = MISS
    const float MAX_DT    = 0.05f;

    // health
    const float HP_MISS   = 0.16f;
    const float HP_PERF   = 0.035f;
    const float HP_GOOD   = 0.015f;
    const int   FEVER_COMBO = 24;

    enum State { Playing, Over }
    State state = State.Playing;

    // ---- note ----
    class Note { public Transform tr; public int lane; public float hitTime; public bool dead; }
    readonly List<Note> notes = new List<Note>();
    readonly Stack<Transform> notePool = new Stack<Transform>();

    // ---- scene refs ----
    Transform cam; Camera camComp;
    Material[] laneMat = new Material[LANES];
    Material[] laneGlowMat = new Material[LANES];
    Transform[] laneGlow = new Transform[LANES];
    float[] laneFlash = new float[LANES];          // per-lane tap glow 0..1
    float[] laneMiss  = new float[LANES];          // per-lane miss red 0..1
    Transform hitBar; Material hitBarMat;
    readonly List<Transform> rungs = new List<Transform>();
    Transform tintQuad;                            // fever/low-hp full-screen tint

    TextMesh hudScore, hudInfo, comboText, judgeText, banner, dbg;
    Transform hpBack, hpFill;

    // ---- run state ----
    float songTime;
    int score, best, combo, maxCombo, level;
    int perfectCount, goodCount, missCount;
    float health = 1f;
    bool fever; float feverFlash;
    float comboPunch, judgeTimer, judgeScale; Color judgeColor;
    float beatPulse;

    // chart generation
    float genTime; int stepIdx;
    float[] lastLaneTime = new float[LANES];
    int lastPlacedLane = -1;

    // input / attract
    bool attract = true;
    float[] laneScreenX = new float[LANES];
    bool prevMouse;

    // hud geometry
    float halfH = 5f, halfW = 9f, hudScale = 1f; bool hudPortrait;
    const float HUD_Z = 6f, FOV = 50f;
    Vector3 camVel;
    bool showDbg; float fps; float lowHpPulse;

    // pentatonic scale for melodic hit tones (C major pentatonic, two octaves)
    static readonly float[] Penta = { 523.25f, 587.33f, 659.25f, 783.99f, 880f, 1046.5f, 1174.66f, 1318.5f };

    static float[] LaneX = new float[LANES];

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        for (int i = 0; i < LANES; i++) LaneX[i] = (i - (LANES - 1) * 0.5f) * SPACING;   // -3,-1,1,3
        best = PlayerPrefs.GetInt("pulse_best", 0);

        BuildEnvironment();
        BuildMaterials();
        BuildHighway();
        BuildCamera();
        BuildHud();
        ResetRun();
    }

    // ===================================================================== materials
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.4f, bool emissive = false, float emi = 0.7f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * emi);
        }
        return m;
    }

    static Material AlphaMat(Color c)
    {
        var sh = Shader.Find("Sprites/Default"); if (sh == null) sh = Shader.Find("Unlit/Transparent");
        return new Material(sh) { color = c };
    }

    static void NoCollide(GameObject g) { var c = g.GetComponent<Collider>(); if (c) Destroy(c); }

    static readonly Color[] LaneCol =
    {
        new Color(0.25f, 0.85f, 1f),    // cyan
        new Color(1f, 0.35f, 0.72f),    // magenta
        new Color(0.55f, 1f, 0.45f),    // green
        new Color(1f, 0.68f, 0.2f),     // amber
    };

    void BuildMaterials()
    {
        for (int i = 0; i < LANES; i++)
        {
            laneMat[i] = Mat(LaneCol[i], 0.1f, 0.7f, true, 1.5f);
            laneGlowMat[i] = Mat(LaneCol[i], 0f, 0.6f, true, 0.4f);
        }
    }

    // ===================================================================== environment
    void BuildEnvironment()
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(0.75f, 0.82f, 1f);
        sun.intensity = 0.85f;
        sun.transform.rotation = Quaternion.Euler(55f, -20f, 0f);
        sun.shadows = LightShadows.None;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.16f, 0.20f, 0.36f);
        RenderSettings.ambientEquatorColor = new Color(0.09f, 0.11f, 0.22f);
        RenderSettings.ambientGroundColor  = new Color(0.03f, 0.03f, 0.08f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.02f, 0.02f, 0.06f);
        RenderSettings.fogStartDistance = 34f;
        RenderSettings.fogEndDistance = 64f;
    }

    // ===================================================================== highway
    void BuildHighway()
    {
        float halfSpan = LANES * SPACING * 0.5f;   // 4.0

        // dark floor
        var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(floor); floor.name = "Floor";
        floor.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        floor.transform.localScale = new Vector3(halfSpan * 2f + 1.4f, FAR_Z + 16f, 1f);
        floor.transform.position = new Vector3(0f, 0f, (FAR_Z + PAST_Z) * 0.5f);
        floor.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.04f, 0.05f, 0.10f), 0f, 0.25f, true, 0.25f);

        // lane divider lines (emissive thin bars running down the track)
        var divMat = Mat(new Color(0.20f, 0.55f, 0.78f), 0f, 0.7f, true, 1.0f);
        for (int i = 0; i <= LANES; i++)
        {
            float x = (i - LANES * 0.5f) * SPACING;      // -4,-2,0,2,4
            var d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            NoCollide(d); d.name = "Div";
            d.transform.localScale = new Vector3(0.07f, 0.02f, FAR_Z + 14f);
            d.transform.position = new Vector3(x, 0.02f, (FAR_Z + PAST_Z) * 0.5f);
            d.GetComponent<Renderer>().sharedMaterial = divMat;
        }

        // per-lane hit-zone glow pads at the hit line (flash on tap)
        for (int i = 0; i < LANES; i++)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Quad);
            NoCollide(g); g.name = "LaneGlow";
            g.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            g.transform.localScale = new Vector3(SPACING * 0.92f, 5.0f, 1f);
            g.transform.position = new Vector3(LaneX[i], 0.03f, HIT_Z + 0.6f);
            g.GetComponent<Renderer>().sharedMaterial = laneGlowMat[i];
            laneGlow[i] = g.transform;
        }

        // the hit line bar
        hitBar = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
        NoCollide(hitBar.gameObject); hitBar.name = "HitBar";
        hitBar.localScale = new Vector3(halfSpan * 2f + 0.5f, 0.16f, 0.42f);
        hitBar.position = new Vector3(0f, 0.12f, HIT_Z);
        hitBarMat = Mat(new Color(1f, 1f, 1f), 0.1f, 0.9f, true, 1.6f);
        hitBar.GetComponent<Renderer>().sharedMaterial = hitBarMat;

        // scrolling beat-rungs across the track (one per beat -> reinforces tempo)
        int rungCount = 14;
        var rungMat = Mat(new Color(0.30f, 0.45f, 0.7f), 0f, 0.6f, true, 0.7f);
        float rungSpacing = Speed * BeatDur;
        for (int i = 0; i < rungCount; i++)
        {
            var r = GameObject.CreatePrimitive(PrimitiveType.Cube);
            NoCollide(r); r.name = "Rung";
            r.transform.localScale = new Vector3(halfSpan * 2f + 1.0f, 0.03f, 0.10f);
            r.transform.position = new Vector3(0f, 0.015f, HIT_Z + i * rungSpacing);
            r.GetComponent<Renderer>().sharedMaterial = rungMat;
            rungs.Add(r.transform);
        }
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.015f, 0.02f, 0.05f);
        camComp.fieldOfView = FOV;
        camComp.farClipPlane = 140f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
        cam.position = new Vector3(0f, 7.5f, -12.5f);
        cam.rotation = Quaternion.Euler(24f, 0f, 0f);
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor, TextAlignment align = TextAlignment.Center)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor; t.alignment = align;
        t.transform.SetParent(cam, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    Transform MakeBar(Color c, float emi)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(q);
        q.GetComponent<Renderer>().sharedMaterial = Mat(c, 0f, 0.5f, true, emi);
        q.transform.SetParent(cam, false);
        q.transform.localRotation = Quaternion.identity;
        return q.transform;
    }

    void BuildHud()
    {
        hudScore = MakeText(0.09f, Color.white, TextAnchor.UpperLeft, TextAlignment.Left);
        hudInfo  = MakeText(0.055f, new Color(0.8f, 0.92f, 1f), TextAnchor.UpperRight, TextAlignment.Right);
        comboText = MakeText(0.14f, new Color(1f, 0.9f, 0.4f), TextAnchor.MiddleCenter);
        judgeText = MakeText(0.1f, Color.white, TextAnchor.MiddleCenter);
        banner   = MakeText(0.12f, Color.white, TextAnchor.MiddleCenter);
        dbg      = MakeText(0.04f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft, TextAlignment.Left);
        dbg.gameObject.SetActive(false);

        hpBack = MakeBar(new Color(0.18f, 0.05f, 0.08f), 0.2f);
        hpFill = MakeBar(new Color(0.4f, 1f, 0.7f), 0.8f);

        // full-screen tint quad (fever warm / low-hp red), behind HUD text
        var t = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(t);
        t.GetComponent<Renderer>().sharedMaterial = AlphaMat(new Color(1f, 0.5f, 0.1f, 0f));
        t.transform.SetParent(cam, false);
        t.transform.localPosition = new Vector3(0f, 0f, HUD_Z + 1.5f);
        t.transform.localScale = new Vector3(90f, 60f, 1f);
        tintQuad = t.transform;

        comboText.text = ""; judgeText.text = ""; banner.text = "";
        AdjustHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.35f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        hudScale = Mathf.Clamp(halfW / 7f, 0.4f, 1.25f);
        bool portrait = aspect < 0.95f;
        hudPortrait = portrait;
        float ix = halfW * 0.95f, topY = halfH * 0.78f;

        hudScore.transform.localPosition = new Vector3(-ix, topY, HUD_Z); hudScore.characterSize = (portrait ? 0.066f : 0.085f) * hudScale;
        hudInfo.transform.localPosition  = new Vector3( ix, topY, HUD_Z); hudInfo.characterSize  = (portrait ? 0.044f : 0.052f) * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -halfH * 0.50f, HUD_Z); dbg.characterSize = 0.038f * hudScale;

        comboText.transform.localPosition = new Vector3(0f, halfH * 0.32f, HUD_Z);
        judgeText.transform.localPosition = new Vector3(0f, -halfH * 0.14f, HUD_Z);
        banner.transform.localPosition    = new Vector3(0f, halfH * 0.05f, HUD_Z); banner.characterSize = (portrait ? 0.066f : 0.10f) * hudScale;

        // health bar: thin strip across the very top edge, above the score/info rows
        float hw = halfW * 1.9f, hh = halfH * 0.026f, hy = halfH * 0.95f;
        hpBack.localPosition = new Vector3(0f, hy, HUD_Z); hpBack.localScale = new Vector3(hw, hh, 1f);
        RefreshHpBar(hw, hh, hy);
    }

    float hpW, hpH, hpY;
    void RefreshHpBar(float w, float h, float y) { hpW = w; hpH = h; hpY = y; RefreshHpBar(); }
    void RefreshHpBar()
    {
        float f = Mathf.Clamp01(health);
        hpFill.localScale = new Vector3(hpW * f, hpH * 0.8f, 1f);
        hpFill.localPosition = new Vector3(-hpW * 0.5f + hpW * f * 0.5f, hpY, HUD_Z - 0.02f);
        var col = Color.Lerp(new Color(1f, 0.3f, 0.3f), new Color(0.4f, 1f, 0.7f), f);
        var r = hpFill.GetComponent<Renderer>();
        if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", col);
        if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", col);
        if (r.sharedMaterial.HasProperty("_EmissionColor")) r.sharedMaterial.SetColor("_EmissionColor", col * 0.8f);
    }

    void SetAlpha(Transform t, float a)
    {
        var r = t.GetComponent<Renderer>(); if (r == null) return;
        var c = r.sharedMaterial.color; c.a = a; r.sharedMaterial.color = c;
    }
    void SetTint(Color c)
    {
        var r = tintQuad.GetComponent<Renderer>(); r.sharedMaterial.color = c;
    }

    // ===================================================================== run reset
    void ResetRun()
    {
        for (int i = notes.Count - 1; i >= 0; i--) ReleaseNote(notes[i]);
        notes.Clear();

        state = State.Playing;
        songTime = 0f; score = 0; combo = 0; maxCombo = 0; level = 1;
        perfectCount = 0; goodCount = 0; missCount = 0;
        health = 1f; fever = false; feverFlash = 0f; lowHpPulse = 0f;
        comboPunch = 0f; judgeTimer = 0f;
        genTime = 2.2f; stepIdx = 0; lastPlacedLane = -1;
        for (int i = 0; i < LANES; i++) { lastLaneTime[i] = -10f; laneFlash[i] = 0f; laneMiss[i] = 0f; }

        attract = true;
        SetTint(new Color(1f, 0.5f, 0.1f, 0f));
        comboText.text = ""; judgeText.text = ""; banner.text = "";
        hudScore.gameObject.SetActive(true); hudInfo.gameObject.SetActive(true);
        RefreshHud(); RefreshHpBar();
    }

    // ===================================================================== update
    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, MAX_DT);
        fps = Mathf.Lerp(fps, 1f / Mathf.Max(0.0001f, Time.deltaTime), 0.1f);

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        ComputeLaneScreenX();
        HandleInput();

        if (state == State.Playing) UpdatePlaying(dt);

        UpdateRungs(dt);
        UpdateCameraAndHud(dt);
        UpdateFx(dt);
        prevMouse = Input.GetMouseButton(0);
    }

    void ComputeLaneScreenX()
    {
        if (camComp == null) return;
        for (int i = 0; i < LANES; i++)
        {
            var sp = camComp.WorldToScreenPoint(new Vector3(LaneX[i], NOTE_Y, HIT_Z));
            laneScreenX[i] = sp.x;
        }
    }

    int LaneByScreenX(float x)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < LANES; i++) { float d = Mathf.Abs(x - laneScreenX[i]); if (d < bd) { bd = d; best = i; } }
        return best;
    }

    // ---------------------------------------------------------------- input
    void HandleInput()
    {
        if (state == State.Over)
        {
            if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Space) ||
                Input.GetMouseButtonDown(0) || AnyTouchBegan())
                ResetRun();
            return;
        }

        // keyboard lane keys
        if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.Alpha1)) HumanTap(0);
        if (Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.Alpha2)) HumanTap(1);
        if (Input.GetKeyDown(KeyCode.J) || Input.GetKeyDown(KeyCode.Alpha3)) HumanTap(2);
        if (Input.GetKeyDown(KeyCode.K) || Input.GetKeyDown(KeyCode.Alpha4)) HumanTap(3);

        // touch — each began touch taps the nearest lane column
        if (Input.touchCount > 0)
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began) HumanTap(LaneByScreenX(t.position.x));
            }
        }
        else if (Input.GetMouseButtonDown(0))
        {
            HumanTap(LaneByScreenX(Input.mousePosition.x));
        }
    }

    bool AnyTouchBegan()
    {
        for (int i = 0; i < Input.touchCount; i++) if (Input.GetTouch(i).phase == TouchPhase.Began) return true;
        return false;
    }

    void HumanTap(int lane)
    {
        attract = false;
        if (state != State.Playing) return;
        TapLane(lane);
    }

    // ---------------------------------------------------------------- playing
    void UpdatePlaying(float dt)
    {
        songTime += dt;
        level = 1 + Mathf.FloorToInt(songTime / 22f);
        GenerateNotes();
        UpdateNotes(dt);

        notes.RemoveAll(n => n.dead);

        fever = combo >= FEVER_COMBO;
        beatPulse = 1f - Mathf.Repeat(songTime, BeatDur) / BeatDur;   // 1 at beat, ramps down

        RefreshHud();
        RefreshHpBar();

        if (health <= 0f) GameOver();
    }

    // ---------------------------------------------------------------- chart generation
    void GenerateNotes()
    {
        float horizon = songTime + TRAVEL + 0.05f;
        float step = BeatDur * 0.5f;               // eighth-note grid
        int guard = 0;
        while (genTime < horizon && guard++ < 64)
        {
            GenStep(genTime, stepIdx);
            stepIdx++;
            genTime += step;
        }
    }

    void GenStep(float t, int idx)
    {
        float d = Mathf.Clamp01(t / 95f);          // difficulty 0..1 over ~95s
        bool onBeat = (idx % 2 == 0);
        float p = onBeat ? (0.60f + 0.34f * d) : (0.08f + 0.40f * d);
        if (Random.value > p) return;

        int lane = PickLane(t, -1);
        if (lane < 0) return;
        PlaceNote(lane, t);
        lastPlacedLane = lane;

        // doubles (two-lane chords) once difficulty is up
        if (onBeat && d > 0.35f && Random.value < 0.08f + 0.20f * d)
        {
            int lane2 = PickLane(t, lane);
            if (lane2 >= 0) PlaceNote(lane2, t);
        }
    }

    int PickLane(float t, int exclude)
    {
        // lanes that have had enough gap since their last note (keeps single-lane streams playable)
        const float minGap = 0.24f;
        int bestLane = -1; float bestW = -1f;
        for (int i = 0; i < LANES; i++)
        {
            if (i == exclude) continue;
            if (t - lastLaneTime[i] < minGap) continue;
            float w = Random.value;
            if (i != lastPlacedLane) w += 0.4f;     // bias toward movement between lanes
            if (w > bestW) { bestW = w; bestLane = i; }
        }
        return bestLane;
    }

    void PlaceNote(int lane, float hitTime)
    {
        lastLaneTime[lane] = hitTime;
        var tr = AcquireNote(lane);
        float rem = hitTime - songTime;
        float z = Mathf.Lerp(HIT_Z, FAR_Z, Mathf.Clamp01(rem / TRAVEL));
        tr.position = new Vector3(LaneX[lane], NOTE_Y, z);
        notes.Add(new Note { tr = tr, lane = lane, hitTime = hitTime, dead = false });
    }

    Transform AcquireNote(int lane)
    {
        Transform t;
        if (notePool.Count > 0) { t = notePool.Pop(); t.gameObject.SetActive(true); }
        else { var g = GameObject.CreatePrimitive(PrimitiveType.Cube); NoCollide(g); g.name = "Note"; t = g.transform; }
        t.localScale = new Vector3(NOTE_W, 0.30f, 0.9f);
        t.rotation = Quaternion.identity;
        t.GetComponent<Renderer>().sharedMaterial = laneMat[lane];
        return t;
    }

    void ReleaseNote(Note n)
    {
        if (n.tr == null) return;
        n.tr.gameObject.SetActive(false);
        notePool.Push(n.tr);
    }

    // ---------------------------------------------------------------- notes update
    void UpdateNotes(float dt)
    {
        for (int i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            if (n.dead) continue;
            float rem = n.hitTime - songTime;
            float z = Mathf.Lerp(HIT_Z, FAR_Z, rem / TRAVEL);
            n.tr.position = new Vector3(LaneX[n.lane], NOTE_Y, z);
            n.tr.Rotate(0f, 0f, 80f * dt, Space.Self);

            if (attract && rem <= 0f) { RegisterHit(n, true); continue; }
            if (rem < -PASS_W) Miss(n);
        }
    }

    // ---------------------------------------------------------------- tap resolution
    void TapLane(int lane)
    {
        laneFlash[lane] = 1f;
        Note best = null; float bestAbs = float.MaxValue;
        for (int i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            if (n.dead || n.lane != lane) continue;
            float a = Mathf.Abs(n.hitTime - songTime);
            if (a < bestAbs) { bestAbs = a; best = n; }
        }
        if (best != null && bestAbs <= GOOD_W)
        {
            RegisterHit(best, bestAbs <= PERFECT_W);
        }
        else
        {
            Juice.Blip(180f, 0.05f, 0.12f);         // dull whiff — no penalty (forgiving)
        }
    }

    void RegisterHit(Note n, bool perfect)
    {
        n.dead = true; ReleaseNote(n);
        combo++; if (combo > maxCombo) maxCombo = combo;
        int mult = 1 + Mathf.Min(combo, 80) / 10;   // 1..9
        int pts = Mathf.RoundToInt((perfect ? 120 : 60) * mult * (fever ? 2f : 1f));
        score += pts;
        health = Mathf.Clamp01(health + (perfect ? HP_PERF : HP_GOOD));

        if (perfect) perfectCount++; else goodCount++;
        comboPunch = 1f;
        laneFlash[n.lane] = 1f;

        // melodic ascending tone — climbs the pentatonic scale with the combo
        int s = (combo - 1) % Penta.Length;
        Juice.Blip(Penta[s] * (fever ? 1.5f : 1f), 0.07f, perfect ? 0.34f : 0.24f);
        if (perfect) Juice.Pop(n.tr.position, LaneCol[n.lane], 8);

        ShowJudge(perfect ? "PERFECT" : "GOOD", perfect ? new Color(1f, 0.95f, 0.4f) : new Color(0.6f, 0.95f, 1f));
        if (combo > 0 && combo % 25 == 0) { Juice.Score(new Vector3(LaneX[n.lane], NOTE_Y, HIT_Z)); feverFlash = 1f; }
    }

    void Miss(Note n)
    {
        n.dead = true; ReleaseNote(n);
        combo = 0; missCount++;
        health = Mathf.Clamp01(health - HP_MISS);
        laneMiss[n.lane] = 1f;
        lowHpPulse = 1f;
        Juice.Hit();
        ShowJudge("MISS", new Color(1f, 0.4f, 0.45f));
    }

    void ShowJudge(string s, Color c) { judgeText.text = s; judgeColor = c; judgeTimer = 0.5f; judgeScale = 1f; }

    // ---------------------------------------------------------------- game over
    void GameOver()
    {
        state = State.Over;
        if (score > best) { best = score; PlayerPrefs.SetInt("pulse_best", best); PlayerPrefs.Save(); }
        Juice.Lose();
        comboText.text = ""; judgeText.text = "";
        hudScore.gameObject.SetActive(false); hudInfo.gameObject.SetActive(false);
        float acc = Accuracy();
        banner.text = "GAME OVER\n\nSCORE  " + score + "\nMAX COMBO  " + maxCombo +
                      "\nACCURACY  " + acc.ToString("0.0") + "%\nBEST  " + best + "\n\nTAP / R  to retry";
    }

    float Accuracy()
    {
        float tot = perfectCount + goodCount + missCount;
        if (tot < 1f) return 100f;
        return (perfectCount + goodCount * 0.5f) / tot * 100f;
    }

    // ---------------------------------------------------------------- rungs
    void UpdateRungs(float dt)
    {
        if (state != State.Playing && state != State.Over) return;
        float move = (state == State.Playing) ? Speed * dt : Speed * dt * 0.15f;
        float rungSpacing = Speed * BeatDur;
        float total = rungs.Count * rungSpacing;
        for (int i = 0; i < rungs.Count; i++)
        {
            var p = rungs[i].position; p.z -= move;
            if (p.z < PAST_Z) p.z += total;
            rungs[i].position = p;
        }
    }

    // ---------------------------------------------------------------- camera + hud refresh
    void UpdateCameraAndHud(float dt)
    {
        if (camComp == null) return;
        AdjustHud();
        float aspect = Mathf.Max(0.35f, camComp.aspect);
        // narrow (portrait) -> pull camera back & up so all 4 lanes stay on screen
        float zoom = Mathf.Clamp(0.82f / aspect, 1.0f, 2.3f);
        Vector3 want = new Vector3(0f, 7.5f * zoom, -12.5f * zoom);
        cam.position = Vector3.SmoothDamp(cam.position, want, ref camVel, 0.15f);
        cam.rotation = Quaternion.Euler(Mathf.Lerp(24f, 30f, Mathf.InverseLerp(1f, 2.3f, zoom)), 0f, 0f);
    }

    // ---------------------------------------------------------------- fx
    void UpdateFx(float dt)
    {
        // hit bar pulses on the beat
        float hb = 1.2f + beatPulse * 1.4f + feverFlash * 1.5f;
        SetEmi(hitBarMat, new Color(1f, 1f, 1f), hb);
        hitBar.localScale = new Vector3(hitBar.localScale.x, 0.16f, 0.42f * (1f + beatPulse * 0.4f));

        // lane tap glow + miss flash
        for (int i = 0; i < LANES; i++)
        {
            laneFlash[i] = Mathf.Max(0f, laneFlash[i] - dt * 3.5f);
            laneMiss[i]  = Mathf.Max(0f, laneMiss[i] - dt * 2.5f);
            float emi = 0.35f + laneFlash[i] * 2.6f;
            Color c = laneMiss[i] > 0.01f ? Color.Lerp(LaneCol[i], new Color(1f, 0.15f, 0.2f), laneMiss[i]) : LaneCol[i];
            SetEmi(laneGlowMat[i], c, emi);
            laneGlow[i].localScale = new Vector3(SPACING * 0.92f, 5.0f * (1f + laneFlash[i] * 0.25f), 1f);
        }

        // combo text
        comboPunch = Mathf.Max(0f, comboPunch - dt * 2.5f);
        feverFlash = Mathf.Max(0f, feverFlash - dt * 1.6f);
        if (combo >= 3 && state == State.Playing)
        {
            comboText.text = (fever ? "FEVER  x" : "COMBO  x") + combo;
            comboText.color = fever ? new Color(1f, 0.5f, 0.5f) : new Color(1f, 0.9f, 0.4f);
            comboText.characterSize = ((hudPortrait ? 0.066f : 0.11f) + comboPunch * 0.04f) * hudScale;
        }
        else comboText.text = "";

        // judge popup fade + pop
        if (judgeTimer > 0f)
        {
            judgeTimer -= dt;
            judgeScale = Mathf.Lerp(judgeScale, 1.15f, dt * 6f);
            judgeText.characterSize = (hudPortrait ? 0.062f : 0.085f) * hudScale * judgeScale;
            var c = judgeColor; c.a = Mathf.Clamp01(judgeTimer / 0.5f); judgeText.color = c;
            if (judgeTimer <= 0f) judgeText.text = "";
        }

        // screen tint: fever warm glow, or low-health red
        lowHpPulse = Mathf.Max(0f, lowHpPulse - dt * 1.8f);
        float feverA = (fever && state == State.Playing) ? 0.10f + feverFlash * 0.12f : 0f;
        float redA = 0f;
        if (state == State.Playing && health < 0.3f) redA = (0.3f - health) * 0.6f + lowHpPulse * 0.18f;
        else redA = lowHpPulse * 0.18f;
        if (redA > feverA) SetTint(new Color(1f, 0.15f, 0.2f, redA));
        else SetTint(new Color(1f, 0.55f, 0.15f, feverA));

        if (showDbg && dbg)
        {
            Note nn = NearestAny(out float ndt);
            dbg.text = string.Format(
                "fps {0:00}  state {1}  song {2:0.0}\nnotes {3} pool {4}  lvl {5}\ncombo {6} max {7} mult {8}  fever {9}\nhp {10:0.00}  P {11} G {12} M {13}\nacc {14:0.0}%  nearestDt {15:0.000}",
                fps, state, songTime, notes.Count, notePool.Count, level,
                combo, maxCombo, 1 + Mathf.Min(combo, 80) / 10, fever,
                health, perfectCount, goodCount, missCount, Accuracy(), ndt);
        }
    }

    Note NearestAny(out float adt)
    {
        Note b = null; float ba = float.MaxValue;
        for (int i = 0; i < notes.Count; i++)
        {
            float a = Mathf.Abs(notes[i].hitTime - songTime);
            if (a < ba) { ba = a; b = notes[i]; }
        }
        adt = b != null ? (b.hitTime - songTime) : 0f;
        return b;
    }

    static void SetEmi(Material m, Color c, float emi)
    {
        if (m == null) return;
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", c * emi);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    void RefreshHud()
    {
        if (hudScore) hudScore.text = "SCORE  " + score;
        if (hudInfo)  hudInfo.text  = "LV " + level + "\nACC " + Accuracy().ToString("0") + "%\nBEST " + best;
    }
}
