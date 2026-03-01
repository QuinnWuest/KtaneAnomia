using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using KModkit;

public class AnomiaScript : MonoBehaviour
{
    public KMBombInfo Bomb;
    public KMAudio Audio;
    public KMBombModule Module;
    public KMGameInfo GameInfo;

    public KMSelectable nextButton;

    public GameObject[] cards;
    public TextMesh[] texts;
    public SpriteRenderer[] symbols;

    public GameObject[] backings;
    public Material[] backingColors;

    public KMSelectable[] iconButtons;
    public SpriteRenderer[] sprites;
    public Sprite[] allSymbols;
    public Sprite BlankSprite;

    static int moduleIdCounter = 1;
    int moduleId;
    private bool moduleSolved;
    private bool _readyToStartup;
    private bool _failedToGenerate;

    private string[] allTexts = new string[] { "Starts with a\nvowel", "Ends with a\nvowel", "Has at least\n1 space", "Ends with the\nletter 's'", "Does not\ncontain 'e'", "Has no spaces", "Starts with a\nletter from A-M", "Ends in a\nletter from A-M", "Contains fewer\nthan 8 letters", "Contains more\nthan 10 letters" };
    string vowels = "AEIOU";
    string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    string[] directions = new string[] { "top", "right", "bottom", "left" };
    int[] numbers = Enumerable.Range(0, 8).ToArray();

    int[] symbolIndices = new int[4];
    int[] stringIndices = new int[4];
    bool[] showingBack = new bool[4];
    bool[] isAnimating = new bool[4];

    bool isFighting;
    int fighter, opponent, stage;

    private Coroutine timer;

    private bool lightsAreOn = true;
    public bool TwitchPlaysActive;
    float timeLimit, warning;
    const float TPTime = 30;
    const float TPWarning = 8;
    float flipSpeed = 12;

    #region Modsettings
    class AnomiaSettings
    {
        public float timer = 10;
        public float warningTime = 3;
    }
    AnomiaSettings settings = new AnomiaSettings();
    private static Dictionary<string, object>[] TweaksEditorSettings = new Dictionary<string, object>[]
    {
          new Dictionary<string, object>
          {
            { "Filename", "AnomiaSettings.json"},
            { "Name", "Anomia" },
            { "Listings", new List<Dictionary<string, object>>
                {
                  new Dictionary<string, object>
                  {
                    { "Key", "timer" },
                    { "Text", "Length of the module's timer."}
                  },
                  new Dictionary<string, object>
                  {
                    { "Key", "warningTime" },
                    { "Text", "Time until the module will play a warning tone."}
                  }
                }
            }
          }
    };
    #endregion

    void Awake()
    {
        //Removes all null entries from the allModules array, in the case that an icon gets deleted.
        moduleId = moduleIdCounter++;
        for (int i = 0; i < 4; i++)
        {
            int ix = i;
            iconButtons[ix].OnInteract += delegate () { IconPress(ix); return false; };
        }
        nextButton.OnInteract += delegate () { Next(); return false; };

        //Changes the timers if TP is active.
        Module.OnActivate += delegate ()
        {
            if (TwitchPlaysActive)
            {
                timeLimit = TPTime;
                warning = TPWarning;
            }
        };

        //Connect into when the room's lights change and sets a variable accordingly.
        GameInfo.OnLightsChange += delegate (bool state)
        {
            if (lightsAreOn != state)
            {
                if (!state)
                    Debug.LogFormat("[Anomia #{0}] Because the lights have turned off, the timer is now paused.", moduleId);
                else
                    Debug.LogFormat("[Anomia #{0}] Lights have turned back on, timer resumed.", moduleId);
                lightsAreOn = state;
            }
        };
        //Shuffles an array of numbers 0-7. These will be used for the initial generation such that no fights happen immediately.0
        numbers.Shuffle();

        //Sets up the modsettings.
        ModConfig<AnomiaSettings> config = new ModConfig<AnomiaSettings>("AnomiaSettings");
        settings = config.Read();
        config.Write(settings);
        timeLimit = settings.timer <= 0 ? 10 : settings.timer; //The timelimit cannot be <= 0.
        warning = settings.warningTime <= 0 || settings.warningTime > timeLimit ? 0.3f * timeLimit : settings.warningTime; //The warning time cannot be <= 0, and it cannot be longer than the time limit.
    }

    void Start()
    {
        IconFetch.Instance.WaitForFetch(OnFetched);
        StartCoroutine(WaitToStartup());
    }

    private void OnFetched(bool error)
    {
        if (error)
        {
            _failedToGenerate = true;
            Debug.LogFormat("[Anomia #{0}] The module failed to fetch the icons. Press any button to solve.", moduleId);
            for (int i = 0; i < 4; i++)
                sprites[i].sprite = BlankSprite;
        }
        _readyToStartup = true;
    }

    private IEnumerator WaitToStartup()
    {
        while (!_readyToStartup)
        {
            yield return null;
            if (_failedToGenerate)
                yield break;
        }
        for (int i = 0; i < 4; i++)
            StartCoroutine(FlipCard(i));//Flips all cards over to their initial states.
        Debug.LogFormat("[Anomia #{0}] INITIAL DISPLAY", moduleId);
        Debug.LogFormat("[Anomia #{0}] The initial cards have symbols {1}.", moduleId, symbolIndices.Select(x => allSymbols[x].name).Join(", "));
        Debug.LogFormat("[Anomia #{0}] The initial messages are {1}.", moduleId, allTexts.Select(x => x.Replace('\n', ' ')).Join(", "));
    }

    void IconPress(int pos)
    {
        iconButtons[pos].AddInteractionPunch(0.3f);
        Audio.PlayGameSoundAtTransform(KMSoundOverride.SoundEffect.ButtonPress, iconButtons[pos].transform);

        //Turns off the timer.
        if (timer != null)
            StopCoroutine(timer);

        if (_failedToGenerate)
        {
            moduleSolved = true;
            Module.HandlePass();
        }

        //If the module is solved, or any card is in the process of flipping over, return now.
        if (moduleSolved || isAnimating.Any(x => x) || !_readyToStartup)
            return;
        //If there's no match going on, nothing is correct. Strike anyways.
        if (!isFighting)
        {
            Module.HandleStrike();
            Debug.LogFormat("[Anomia #{0}] Attempted to press a module while no match was active. Strike incurred.", moduleId);
        }
        //Otherwise, we *are* fighting, so check the validity of the pressed icon against the opponent's card. 
        else if (CheckValidity(opponent, _moduleList[modIxs[pos]][0]))
        {
            //As far as we know, the fight is probably over. If another occurs, we'll turn this back on in 2 lines.
            isFighting = false;
            //Flip over the opponent's card, generating a new icon and symbol.
            StartCoroutine(FlipCard(opponent));
            //Check if there's a duplicate. If there is, we're going to start another fight, and thus set isFighting back to true.
            CheckFights();
            //Put icons back on the buttons.
            GenerateIcons();
        }
        else
        {
            Module.HandleStrike();
            //Stop the timer. Again, if we start another fight, this'll get restarted.
            StopCoroutine(timer);
            Debug.LogFormat("[Anomia #{0}] Attempted to choose a module which did not fit the category. Strike incurred.", moduleId);
            //Flip over *our* card. Then check the duplicates and but the icons back on.
            StartCoroutine(FlipCard(fighter));
            CheckFights();
            GenerateIcons();
        }
    }
    void Next()
    {
        nextButton.AddInteractionPunch(1);
        Audio.PlayGameSoundAtTransform(KMSoundOverride.SoundEffect.ButtonPress, nextButton.transform);
        //If the module is solved, or any card is flipping over, return here.
        if (_failedToGenerate)
        {
            moduleSolved = true;
            Module.HandlePass();
        }
        if (moduleSolved || isAnimating.Any(x => x) || !_readyToStartup)
            return;
        //If we're not in the middle of a fight, we can move onto the next stage.
        else if (!isFighting)
        {
            //If we're on the 12th stage, the module will solve now
            if (stage == 12)
                StartCoroutine(Solve());
            else
            {
                //Note that the current stage % 4 is the player who the defuser will be controlling.
                //Flip over the new card (generates a new symbol and rule)
                StartCoroutine(FlipCard(stage % 4));
                //Sets the new background to blue.
                backings[stage % 4].GetComponent<MeshRenderer>().material = backingColors[1];
                //Sets the background of the previous player back to gray.
                backings[(stage + 3) % 4].GetComponent<MeshRenderer>().material = backingColors[0];
                stage++;
                Debug.LogFormat("[Anomia #{0}] ::STAGE {1}::", moduleId, stage);
                //Checks if the new flipped over card causes duplicates.
                CheckFights();
                GenerateIcons();
            }
        }
        else
        {
            Module.HandleStrike();
            Debug.LogFormat("[Anomia #{0}] Arrow button pressed while a match was happening. Strike incurred.", moduleId);
        }
    }

    private readonly Texture2D[] Textures = new Texture2D[4];
    private int[] modIxs;

    void GenerateIcons()
    {
        if (_failedToGenerate)
            return;
        //Shuffles the array of every sprite.

        var numbersToChooseFrom = Enumerable.Range(0, _moduleList.Length).ToArray().Shuffle();
        var tempModNames = new string[4];

        //Fills the first 3 entries of the array with random modules.
        for (int i = 0; i < 3; i++)
            tempModNames[i] = _moduleList[numbersToChooseFrom[i]][0];
        //If we are in a fight, make sure that the 4th entry is a valid module. Otherwise, just pick a module at random.
        if (isFighting)
            tempModNames[3] = _moduleList.Where(x => CheckValidity(opponent, x[0].Replace('_', '�'))).PickRandom()[0];
        else
            tempModNames[3] = _moduleList[numbersToChooseFrom[3]][0];

        //Shuffle this new array so that the guaranteed valid module is at a random place.
        tempModNames.Shuffle();
        modIxs = tempModNames.Select(x => Array.IndexOf(_moduleList.Select(i => i[0]).ToArray(), x)).ToArray();

        for (int i = 0; i < Textures.Length; i++)
        {
            Textures[i] = IconFetch.Instance.GetIcon(_moduleList[modIxs[i]][1]);
            Textures[i].wrapMode = TextureWrapMode.Clamp;
            Textures[i].filterMode = FilterMode.Point;
            sprites[i].sprite = Sprite.Create(Textures[i], new Rect(0.0f, 0.0f, Textures[i].width, Textures[i].height), new Vector2(0.5f, 0.5f), 100.0f);
        }

        //If we get a fight, log the valid modules.
        if (isFighting)
        {
            List<string> validNames = new List<string>();
            //Takes the names of the icons and checks their validity. If they are valid, add them to the list.
            for (int i = 0; i < 4; i++)
                if (CheckValidity(opponent, _moduleList[modIxs[i]][0]))
                    validNames.Add(_moduleList[modIxs[i]][0]);
            Debug.LogFormat("[Anomia #{0}] The displayed module names are {1}.", moduleId, Enumerable.Range(0, 4).Select(x => _moduleList[modIxs[x]][0]).Join(", "));
            Debug.LogFormat("[Anomia #{0}] The correct possible modules are {1}.", moduleId, validNames.Join(", "));
        }
    }

    void CheckFights()
    {
        if (_failedToGenerate)
            return;

        //If there are not 4 unique symbols, i.e. there's a duplicate, enter a fight.
        if (symbolIndices.Distinct().Count() != 4)
        {
            isFighting = true;
            timer = StartCoroutine(Timer());
            //Gets the symbol which appears twice.
            int duplicate = symbolIndices.Where(x => symbolIndices.Count(y => y == x) == 2).First();
            //Sets the player who the defuser will play as.
            for (int i = 0; i < 4; i++)
            {
                //Requires that +3 or else the clockwise movement will start one ahead of where it should.
                int num = (i + stage + 3) % 4;
                if (symbolIndices[num] == duplicate)
                {
                    fighter = num;
                    break;
                }
            }
            //Sets the player who the defuser will go up against. This is the card whose rule we're using.
            for (int i = 0; i < 4; i++)
            {
                int num = (i + stage + 3) % 4;
                //The fighter and opponent cannot be the same, so skip over the fighter.
                if (num != fighter && symbolIndices[num] == duplicate)
                {
                    opponent = num;
                    break;
                }
            }
            Debug.LogFormat("[Anomia #{0}] A match has started between the {1} player and the {2} player.", moduleId, directions[fighter], directions[opponent]);
        }
        else isFighting = false;
    }

    bool CheckValidity(int cardNum, string input)
    {
        string name = input.ToUpperInvariant();
        //Looks at the rule of the entered card.
        switch (stringIndices[cardNum])
        {
            case 0: return vowels.Contains(name.First()); //First letter is vowel
            case 1: return vowels.Contains(name.Last()); //Last letter is vowel
            case 2: return name.Contains(' '); //Contains a space
            case 3: return name.Last() == 'S'; //Last letter is S
            case 4: return !name.Contains('E'); //Does not contain an E
            case 5: return !name.Contains(' '); //Contains no spaces.
            case 6: return "ABCDEFGHIJKLM".Contains(name.First()); //First letter is A-M
            case 7: return "ABCDEFGHIJKLM".Contains(name.Last()); //Last letter is A-M
            case 8: return name.Count(x => char.IsLetter(x)) < 8; //Name is <8 letters long.    Requires filtering by only letters or else spaces and other stuff will count.
            case 9: return name.Count(x => char.IsLetter(x)) > 10; //Name is >10 letters long.  
            default: throw new ArgumentOutOfRangeException("stringIndices[cardNum]");
        }
    }

    IEnumerator Solve()
    {
        moduleSolved = true;
        for (int i = 0; i < 4; i++)
        {
            //Flips each card over.
            StartCoroutine(FlipCard(i));
            //Gets an index, which refers to either the front (i) or back (i + 4) of the card.
            int cardFace = showingBack[i] ? i + 4 : i;
            //Clears the text and symbol of the chosen face..
            texts[cardFace].text = string.Empty;
            symbols[cardFace].sprite = null;
            //Unrelated to the cards; clears the icon buttons.
            sprites[i].sprite = null;
        }
        yield return new WaitForSeconds(0.5f);
        Module.HandlePass();
        Audio.PlaySoundAtTransform("Solve", transform);
        //Turns each backing green.
        for (int i = 0; i < 4; i++)
            backings[i].GetComponent<MeshRenderer>().material = backingColors[2];
    }

    IEnumerator FlipCard(int pos)
    {
        if (isAnimating[pos])
            yield break;
        isAnimating[pos] = true;
        Audio.PlaySoundAtTransform("Flip", cards[pos].transform);
        //Retrieves an index of the card face. Faces are in order UP-FRONT, RIGHT-FRONT, DOWN-FRONT, LEFT-FRONT, UP-BACK, RIGHT-BACK, DOWN-BACK, LEFT-BACK
        //If the card is currently showing its back, we should change the front, and vice versa.
        int faceAffected = showingBack[pos] ? pos : pos + 4;
        //If we are on the initial stage, use the values from `numbers`, which contains no duplicates. Otherwise, just use a random number.
        symbolIndices[pos] = (stage == 0) ? numbers[pos] : UnityEngine.Random.Range(0, 8);
        //Sets the rule of the card to a random one.
        stringIndices[pos] = UnityEngine.Random.Range(0, allTexts.Length);

        //Sets the text and symbol of the card to the rule & symbol.
        texts[faceAffected].text = allTexts[stringIndices[pos]];
        symbols[faceAffected].sprite = allSymbols[symbolIndices[pos]];

        if (stage != 0 && !moduleSolved)
            Debug.LogFormat("[Anomia #{0}] The {1} card flipped over. Its symbol is {2} and its message is {3}",
                moduleId, directions[pos], allSymbols[symbolIndices[pos]].name, allTexts[stringIndices[pos]].Replace('\n', ' '));

        isAnimating[pos] = true;
        showingBack[pos] = !showingBack[pos];
        Transform TF = cards[pos].transform;

        //Sets up variables. rotation variables depend on which face is being shown.
        Vector3 startRot = (showingBack[pos] ? 360 : 180) * Vector3.up;
        Vector3 endRot = (showingBack[pos] ? 180 : 0) * Vector3.up;
        Vector3 startPos = TF.localPosition;
        //height of the card
        const float endpos = -6;

        //Length of the flip animation
        const float duration = 0.75f;
        //Equal to the process along the animation between 0-1
        float delta = 0;
        while (delta < 1)
        {
            delta += Time.deltaTime / duration;
            TF.localPosition = new Vector3(startPos.x, startPos.y, InOutLerp(startPos.z, endpos, delta));
            TF.localEulerAngles = Vector3.Lerp(startRot, endRot, delta);
            yield return null;
        }
        isAnimating[pos] = false;
    }
    //Lerp method that goes from 0-1 then 1-0. If the graph of Lerp is f(x)= x, then this is f(x)= -|2x-1|+1
    private float InOutLerp(float start, float end, float t)
    {
        if (t < 0)
            t = 0;
        if (t > 1)
            t = 1;
        if (t <= 0.5)
            return Mathf.Lerp(start, end, t * 2);
        else return Mathf.Lerp(end, start, t * 2 - 1);
    }
    IEnumerator Timer()
    {
        float currentTime = 0f;
        //Used to determine if we've made the warning noise yet.
        bool playedWarning = false;
        while (true)
        {
            //If the lights are off, don't progress the timer.
            if (lightsAreOn)
                currentTime += Time.deltaTime;
            if (currentTime > timeLimit - warning && !playedWarning)
            {
                Audio.PlaySoundAtTransform("Warning", transform);
                playedWarning = true;
            }
            //If the elapsed time has past the time limit, we've ran out of time.
            if (currentTime > timeLimit)
            {
                StopCoroutine(timer);
                Module.HandleStrike();
                Debug.LogFormat("[Anomia #{0}] Timer expired, strike incurred.", moduleId);
                StartCoroutine(FlipCard(fighter));
                CheckFights();
                GenerateIcons();
            }
            yield return null;
        }
    }
    //Gets the name of the icon on a certain icon button.

#pragma warning disable 414
    private readonly string TwitchHelpMessage = @"Use !{0} press 1/2/3/4 to press the button in that position in reading order. Use {0} next to press the arrow buton. On TP, the timer is extended to 30 seconds.";
#pragma warning restore 414
    IEnumerator Press(KMSelectable btn, float delay)
    {
        btn.OnInteract();
        yield return new WaitForSeconds(delay);
    }
    IEnumerator ProcessTwitchCommand(string input)
    {
        input = input.Trim().ToUpperInvariant();
        string[] possibleCommands = { "1", "2", "3", "4", "TL", "TR", "BL", "BR" };
        Match m = Regex.Match(input, @"^(PRESS\s+)?([1-4]|[TB][LR])$");
        if (input == "NEXT")
        {
            yield return null;
            yield return Press(nextButton, 0.1f);
        }
        else if (m.Success)
        {
            int selectedBtn = Array.IndexOf(possibleCommands, m.Groups[2].Value) % 4;
            yield return null;
            yield return Press(iconButtons[selectedBtn], 0.1f);
        }
    }

    IEnumerator TwitchHandleForcedSolve()
    {
        while (!moduleSolved)
        {
            if (!isFighting)
            {
                while (isAnimating.Any(x => x))
                    yield return true;
                yield return Press(nextButton, 0.1f);
            }
            else
            {
                //Presses the first icon button which has a valid icon.
                int validBtn = -1;
                for (int i = 0; i < 4; i++)
                {
                    if (CheckValidity(opponent, _moduleList[modIxs[i]][0]))
                        validBtn = i;
                }
                yield return Press(iconButtons[validBtn], 0.1f);
                yield return null;
            }
        }
    }
    private static T[] NewArray<T>(params T[] array) { return array; }
    private static readonly string[][] _moduleList = NewArray
    (
        new string[] { "Capacitor Discharge", "NeedyCapacitor" },
        new string[] { "Complicated Wires", "Venn" },
        new string[] { "Keypad", "Keypad" },
        new string[] { "Knob", "NeedyKnob" },
        new string[] { "Maze", "Maze" },
        new string[] { "Memory", "Memory" },
        new string[] { "Morse Code", "Morse" },
        new string[] { "Password", "Password" },
        new string[] { "Simon Says", "Simon" },
        new string[] { "The Button", "BigButton" },
        new string[] { "Venting Gas", "NeedyVentGas" },
        new string[] { "Who's on First", "WhosOnFirst" },
        new string[] { "Wire Sequence", "WireSequence" },
        new string[] { "Wires", "Wires" },
        new string[] { "Colour Flash", "ColourFlash" },
        new string[] { "Piano Keys", "PianoKeys" },
        new string[] { "Semaphore", "Semaphore" },
        new string[] { "Emoji Math", "Emoji Math" },
        new string[] { "Math", "Needy Math" },
        new string[] { "Lights Out", "LightsOut" },
        new string[] { "Switches", "switchModule" },
        new string[] { "Two Bits", "TwoBits" },
        new string[] { "Anagrams", "AnagramsModule" },
        new string[] { "Word Scramble", "WordScrambleModule" },
        new string[] { "Combination Lock", "combinationLock" },
        new string[] { "Filibuster", "Filibuster" },
        new string[] { "Motion Sense", "MotionSense" },
        new string[] { "Answering Questions", "NeedyVentV2" },
        new string[] { "Foreign Exchange Rates", "ForeignExchangeRates" },
        new string[] { "Listening", "Listening" },
        new string[] { "Round Keypad", "KeypadV2" },
        new string[] { "Connection Check", "graphModule" },
        new string[] { "Morsematics", "MorseV2" },
        new string[] { "Orientation Cube", "OrientationCube" },
        new string[] { "Forget Me Not", "MemoryV2" },
        new string[] { "Letter Keys", "LetterKeys" },
        new string[] { "Astrology", "spwizAstrology" },
        new string[] { "Rotary Phone", "NeedyKnobV2" },
        new string[] { "Logic", "Logic" },
        new string[] { "Adventure Game", "spwizAdventureGame" },
        new string[] { "Crazy Talk", "CrazyTalk" },
        new string[] { "Mystic Square", "MysticSquareModule" },
        new string[] { "Turn The Key", "TurnTheKey" },
        new string[] { "Cruel Piano Keys", "CruelPianoKeys" },
        new string[] { "Plumbing", "MazeV2" },
        new string[] { "Safety Safe", "PasswordV2" },
        new string[] { "Tetris", "spwizTetris" },
        new string[] { "Chess", "ChessModule" },
        new string[] { "Cryptography", "CryptModule" },
        new string[] { "Turn The Keys", "TurnTheKeyAdvanced" },
        new string[] { "Mouse In The Maze", "MouseInTheMaze" },
        new string[] { "Silly Slots", "SillySlots" },
        new string[] { "Number Pad", "NumberPad" },
        new string[] { "Simon States", "SimonV2" },
        new string[] { "Laundry", "Laundry" },
        new string[] { "Alphabet", "alphabet" },
        new string[] { "Probing", "Probing" },
        new string[] { "Caesar Cipher", "CaesarCipherModule" },
        new string[] { "Resistors", "resistors" },
        new string[] { "Skewed Slots", "SkewedSlotsModule" },
        new string[] { "Microcontroller", "Microcontroller" },
        new string[] { "Perspective Pegs", "spwizPerspectivePegs" },
        new string[] { "Murder", "murder" },
        new string[] { "The Gamepad", "TheGamepadModule" },
        new string[] { "Tic Tac Toe", "TicTacToeModule" },
        new string[] { "Monsplode, Fight!", "monsplodeFight" },
        new string[] { "Who's That Monsplode?", "monsplodeWho" },
        new string[] { "Shape Shift", "shapeshift" },
        new string[] { "Follow the Leader", "FollowTheLeaderModule" },
        new string[] { "Friendship", "FriendshipModule" },
        new string[] { "The Bulb", "TheBulbModule" },
        new string[] { "Blind Alley", "BlindAlleyModule" },
        new string[] { "English Test", "EnglishTest" },
        new string[] { "Sea Shells", "SeaShells" },
        new string[] { "Rock-Paper-Scissors-Lizard-Spock", "RockPaperScissorsLizardSpockModule" },
        new string[] { "Square Button", "ButtonV2" },
        new string[] { "Hexamaze", "HexamazeModule" },
        new string[] { "Bitmaps", "BitmapsModule" },
        new string[] { "Colored Squares", "ColoredSquaresModule" },
        new string[] { "Adjacent Letters", "AdjacentLettersModule" },
        new string[] { "Third Base", "ThirdBase" },
        new string[] { "Souvenir", "SouvenirModule" },
        new string[] { "Word Search", "WordSearchModule" },
        new string[] { "Broken Buttons", "BrokenButtonsModule" },
        new string[] { "Simon Screams", "SimonScreamsModule" },
        new string[] { "Modules Against Humanity", "ModuleAgainstHumanity" },
        new string[] { "Complicated Buttons", "complicatedButtonsModule" },
        new string[] { "Battleship", "BattleshipModule" },
        new string[] { "Symbolic Password", "symbolicPasswordModule" },
        new string[] { "Text Field", "TextField" },
        new string[] { "Wire Placement", "WirePlacementModule" },
        new string[] { "Double-Oh", "DoubleOhModule" },
        new string[] { "Cheap Checkout", "CheapCheckoutModule" },
        new string[] { "Coordinates", "CoordinatesModule" },
        new string[] { "Light Cycle", "LightCycleModule" },
        new string[] { "HTTP Response", "http" },
        new string[] { "Color Math", "colormath" },
        new string[] { "Rhythms", "MusicRhythms" },
        new string[] { "Only Connect", "OnlyConnectModule" },
        new string[] { "Neutralization", "neutralization" },
        new string[] { "Web Design", "webDesign" },
        new string[] { "Chord Qualities", "ChordQualities" },
        new string[] { "Creation", "CreationModule" },
        new string[] { "Rubik's Cube", "RubiksCubeModule" },
        new string[] { "FizzBuzz", "fizzBuzzModule" },
        new string[] { "The Clock", "TheClockModule" },
        new string[] { "LED Encryption", "LEDEnc" },
        new string[] { "Bitwise Operations", "BitOps" },
        new string[] { "Edgework", "EdgeworkModule" },
        new string[] { "Fast Math", "fastMath" },
        new string[] { "Minesweeper", "MinesweeperModule" },
        new string[] { "Zoo", "ZooModule" },
        new string[] { "Binary LEDs", "BinaryLeds" },
        new string[] { "Boolean Venn Diagram", "booleanVennModule" },
        new string[] { "Point of Order", "PointOfOrderModule" },
        new string[] { "Ice Cream", "iceCreamModule" },
        new string[] { "Hex To Decimal", "EternitySDec" },
        new string[] { "The Screw", "screw" },
        new string[] { "Yahtzee", "YahtzeeModule" },
        new string[] { "X-Ray", "XRayModule" },
        new string[] { "QR Code", "QRCode" },
        new string[] { "Button Masher", "buttonMasherNeedy" },
        new string[] { "Random Number Generator", "rng" },
        new string[] { "Color Morse", "ColorMorseModule" },
        new string[] { "Mastermind Cruel", "Mastermind Cruel" },
        new string[] { "Mastermind Simple", "Mastermind Simple" },
        new string[] { "Gridlock", "GridlockModule" },
        new string[] { "Big Circle", "BigCircle" },
        new string[] { "Morse-A-Maze", "MorseAMaze" },
        new string[] { "Colored Switches", "ColoredSwitchesModule" },
        new string[] { "Perplexing Wires", "PerplexingWiresModule" },
        new string[] { "Monsplode Trading Cards", "monsplodeCards" },
        new string[] { "Game of Life Simple", "GameOfLifeSimple" },
        new string[] { "Game of Life Cruel", "GameOfLifeCruel" },
        new string[] { "Nonogram", "NonogramModule" },
        new string[] { "Refill that Beer!", "NeedyBeer" },
        new string[] { "S.E.T.", "SetModule" },
        new string[] { "Color Generator", "Color Generator" },
        new string[] { "Painting", "Painting" },
        new string[] { "Shape Memory", "needyShapeMemory" },
        new string[] { "Symbol Cycle", "SymbolCycleModule" },
        new string[] { "Hunting", "hunting" },
        new string[] { "Extended Password", "ExtendedPassword" },
        new string[] { "Curriculum", "curriculum" },
        new string[] { "Braille", "BrailleModule" },
        new string[] { "Mafia", "MafiaModule" },
        new string[] { "Festive Piano Keys", "FestivePianoKeys" },
        new string[] { "Flags", "FlagsModule" },
        new string[] { "Timezone", "timezone" },
        new string[] { "Polyhedral Maze", "PolyhedralMazeModule" },
        new string[] { "Poker", "Poker" },
        new string[] { "Symbolic Coordinates", "symbolicCoordinates" },
        new string[] { "Poetry", "poetry" },
        new string[] { "Sonic the Hedgehog", "sonic" },
        new string[] { "Button Sequence", "buttonSequencesModule" },
        new string[] { "Algebra", "algebra" },
        new string[] { "Visual Impairment", "visual_impairment" },
        new string[] { "The Jukebox", "jukebox" },
        new string[] { "Identity Parade", "identityParade" },
        new string[] { "Backgrounds", "Backgrounds" },
        new string[] { "Blind Maze", "BlindMaze" },
        new string[] { "Maintenance", "maintenance" },
        new string[] { "Mortal Kombat", "mortalKombat" },
        new string[] { "Faulty Backgrounds", "FaultyBackgrounds" },
        new string[] { "Mashematics", "mashematics" },
        new string[] { "Modern Cipher", "modernCipher" },
        new string[] { "Radiator", "radiator" },
        new string[] { "LED Grid", "ledGrid" },
        new string[] { "Sink", "Sink" },
        new string[] { "The iPhone", "iPhone" },
        new string[] { "The Swan", "theSwan" },
        new string[] { "Waste Management", "wastemanagement" },
        new string[] { "Human Resources", "HumanResourcesModule" },
        new string[] { "Skyrim", "skyrim" },
        new string[] { "Burglar Alarm", "burglarAlarm" },
        new string[] { "Press X", "PressX" },
        new string[] { "Error Codes", "errorCodes" },
        new string[] { "European Travel", "europeanTravel" },
        new string[] { "Rapid Buttons", "rapidButtons" },
        new string[] { "LEGOs", "LEGOModule" },
        new string[] { "Rubik's Clock", "rubiksClock" },
        new string[] { "Font Select", "FontSelect" },
        new string[] { "Pie", "pieModule" },
        new string[] { "The Stopwatch", "stopwatch" },
        new string[] { "Forget Everything", "HexiEvilFMN" },
        new string[] { "Logic Gates", "logicGates" },
        new string[] { "The London Underground", "londonUnderground" },
        new string[] { "The Wire", "wire" },
        new string[] { "Color Decoding", "Color Decoding" },
        new string[] { "Grid Matching", "GridMatching" },
        new string[] { "The Sun", "sun" },
        new string[] { "Playfair Cipher", "Playfair" },
        new string[] { "Tangrams", "Tangrams" },
        new string[] { "Cooking", "cooking" },
        new string[] { "The Number", "theNumber" },
        new string[] { "Superlogic", "SuperlogicModule" },
        new string[] { "The Moon", "moon" },
        new string[] { "The Cube", "cube" },
        new string[] { "Dr. Doctor", "DrDoctorModule" },
        new string[] { "Tax Returns", "taxReturns" },
        new string[] { "The Jewel Vault", "jewelVault" },
        new string[] { "Digital Root", "digitalRoot" },
        new string[] { "Graffiti Numbers", "graffitiNumbers" },
        new string[] { "Marble Tumble", "MarbleTumbleModule" },
        new string[] { "X01", "X01" },
        new string[] { "Logical Buttons", "logicalButtonsModule" },
        new string[] { "The Code", "theCodeModule" },
        new string[] { "Tap Code", "tapCode" },
        new string[] { "Simon Sends", "SimonSendsModule" },
        new string[] { "Simon Sings", "SimonSingsModule" },
        new string[] { "Greek Calculus", "greekCalculus" },
        new string[] { "Synonyms", "synonyms" },
        new string[] { "Simon Shrieks", "SimonShrieksModule" },
        new string[] { "Complex Keypad", "complexKeypad" },
        new string[] { "Lasers", "lasers" },
        new string[] { "Subways", "subways" },
        new string[] { "Turtle Robot", "turtleRobot" },
        new string[] { "Guitar Chords", "guitarChords" },
        new string[] { "Calendar", "calendar" },
        new string[] { "USA Maze", "USA" },
        new string[] { "Binary Tree", "binaryTree" },
        new string[] { "The Time Keeper", "timeKeeper" },
        new string[] { "Black Hole", "BlackHoleModule" },
        new string[] { "Lightspeed", "lightspeed" },
        new string[] { "Simon's Star", "simonsStar" },
        new string[] { "Morse War", "MorseWar" },
        new string[] { "Maze Scrambler", "MazeScrambler" },
        new string[] { "Mineseeker", "mineseeker" },
        new string[] { "The Stock Market", "stockMarket" },
        new string[] { "The Number Cipher", "numberCipher" },
        new string[] { "Alphabet Numbers", "alphabetNumbers" },
        new string[] { "British Slang", "britishSlang" },
        new string[] { "Double Color", "doubleColor" },
        new string[] { "Equations", "equations" },
        new string[] { "Maritime Flags", "MaritimeFlagsModule" },
        new string[] { "Determinants", "determinant" },
        new string[] { "Pattern Cube", "PatternCubeModule" },
        new string[] { "Know Your Way", "KnowYourWay" },
        new string[] { "Splitting The Loot", "SplittingTheLootModule" },
        new string[] { "Character Shift", "characterShift" },
        new string[] { "Simon Samples", "simonSamples" },
        new string[] { "Dragon Energy", "dragonEnergy" },
        new string[] { "Uncolored Squares", "UncoloredSquaresModule" },
        new string[] { "Flashing Lights", "flashingLights" },
        new string[] { "Synchronization", "SynchronizationModule" },
        new string[] { "The Switch", "BigSwitch" },
        new string[] { "Reverse Morse", "reverseMorse" },
        new string[] { "Manometers", "manometers" },
        new string[] { "Shikaku", "shikaku" },
        new string[] { "Wire Spaghetti", "wireSpaghetti" },
        new string[] { "Module Homework", "KritHomework" },
        new string[] { "Tennis", "TennisModule" },
        new string[] { "Benedict Cumberbatch", "benedictCumberbatch" },
        new string[] { "Boggle", "boggle" },
        new string[] { "Horrible Memory", "horribleMemory" },
        new string[] { "Signals", "Signals" },
        new string[] { "Command Prompt", "KritCMDPrompt" },
        new string[] { "Boolean Maze", "boolMaze" },
        new string[] { "Sonic & Knuckles", "sonicKnuckles" },
        new string[] { "Quintuples", "quintuples" },
        new string[] { "The Sphere", "sphere" },
        new string[] { "Coffeebucks", "coffeebucks" },
        new string[] { "Colorful Madness", "ColorfulMadness" },
        new string[] { "Bases", "bases" },
        new string[] { "Lion's Share", "LionsShareModule" },
        new string[] { "Snooker", "snooker" },
        new string[] { "Blackjack", "KritBlackjack" },
        new string[] { "Party Time", "PartyTime" },
        new string[] { "Accumulation", "accumulation" },
        new string[] { "The Plunger Button", "plungerButton" },
        new string[] { "The Digit", "TheDigitModule" },
        new string[] { "The Jack-O'-Lantern", "jackOLantern" },
        new string[] { "T-Words", "tWords" },
        new string[] { "Divided Squares", "DividedSquaresModule" },
        new string[] { "Connection Device", "KritConnectionDev" },
        new string[] { "Instructions", "instructions" },
        new string[] { "Valves", "valves" },
        new string[] { "Blockbusters", "blockbusters" },
        new string[] { "Catchphrase", "catchphrase" },
        new string[] { "Countdown", "countdown" },
        new string[] { "Cruel Countdown", "cruelCountdown" },
        new string[] { "Encrypted Morse", "EncryptedMorse" },
        new string[] { "The Crystal Maze", "crystalMaze" },
        new string[] { "IKEA", "qSwedishMaze" },
        new string[] { "Retirement", "retirement" },
        new string[] { "Periodic Table", "periodicTable" },
        new string[] { "Schlag den Bomb", "qSchlagDenBomb" },
        new string[] { "Mahjong", "MahjongModule" },
        new string[] { "Kudosudoku", "KudosudokuModule" },
        new string[] { "The Radio", "KritRadio" },
        new string[] { "Modulo", "modulo" },
        new string[] { "Number Nimbleness", "numberNimbleness" },
        new string[] { "Challenge & Contact", "challengeAndContact" },
        new string[] { "Pay Respects", "lgndPayRespects" },
        new string[] { "The Triangle", "triangle" },
        new string[] { "Sueet Wall", "SueetWall" },
        new string[] { "Christmas Presents", "christmasPresents" },
        new string[] { "Hot Potato", "HotPotato" },
        new string[] { "Functions", "qFunctions" },
        new string[] { "Hieroglyphics", "hieroglyphics" },
        new string[] { "Needy Mrs Bob", "needyMrsBob" },
        new string[] { "Scripting", "KritScripts" },
        new string[] { "Simon Spins", "SimonSpinsModule" },
        new string[] { "Cursed Double-Oh", "CursedDoubleOhModule" },
        new string[] { "Ten-Button Color Code", "TenButtonColorCode" },
        new string[] { "Crackbox", "CrackboxModule" },
        new string[] { "Street Fighter", "streetFighter" },
        new string[] { "The Labyrinth", "labyrinth" },
        new string[] { "Color Match", "lgndColorMatch" },
        new string[] { "Spinning Buttons", "spinningButtons" },
        new string[] { "The Festive Jukebox", "festiveJukebox" },
        new string[] { "Skinny Wires", "skinnyWires" },
        new string[] { "The Hangover", "hangover" },
        new string[] { "Binary Puzzle", "BinaryPuzzleModule" },
        new string[] { "Factory Maze", "factoryMaze" },
        new string[] { "Broken Guitar Chords", "BrokenGuitarChordsModule" },
        new string[] { "Dominoes", "dominoes" },
        new string[] { "Hogwarts", "HogwartsModule" },
        new string[] { "Regular Crazy Talk", "RegularCrazyTalkModule" },
        new string[] { "Simon Speaks", "SimonSpeaksModule" },
        new string[] { "Discolored Squares", "DiscoloredSquaresModule" },
        new string[] { "Krazy Talk", "krazyTalk" },
        new string[] { "Flip The Coin", "KritFlipTheCoin" },
        new string[] { "Numbers", "Numbers" },
        new string[] { "Alchemy", "JuckAlchemy" },
        new string[] { "Cookie Jars", "cookieJars" },
        new string[] { "Free Parking", "freeParking" },
        new string[] { "Simon's Stages", "simonsStages" },
        new string[] { "Varicolored Squares", "VaricoloredSquaresModule" },
        new string[] { "Simon Squawks", "simonSquawks" },
        new string[] { "Zoni", "lgndZoni" },
        new string[] { "Mad Memory", "MadMemory" },
        new string[] { "Unrelated Anagrams", "unrelatedAnagrams" },
        new string[] { "Bartending", "BartendingModule" },
        new string[] { "Question Mark", "Questionmark" },
        new string[] { "Decolored Squares", "DecoloredSquaresModule" },
        new string[] { "Flavor Text EX", "FlavorTextCruel" },
        new string[] { "Flavor Text", "FlavorText" },
        new string[] { "Shapes And Bombs", "ShapesBombs" },
        new string[] { "Homophones", "homophones" },
        new string[] { "DetoNATO", "Detonato" },
        new string[] { "Air Traffic Controller", "NeedyAirTrafficController" },
        new string[] { "SYNC-125 [3]", "sync125_3" },
        new string[] { "Morse Identification", "lgndMorseIdentification" },
        new string[] { "Westeros", "westeros" },
        new string[] { "LED Math", "lgndLEDMath" },
        new string[] { "Pigpen Rotations", "pigpenRotations" },
        new string[] { "Alphabetical Order", "alphabeticOrder" },
        new string[] { "Simon Sounds", "simonSounds" },
        new string[] { "The Fidget Spinner", "theFidgetSpinner" },
        new string[] { "Simon's Sequence", "simonsSequence" },
        new string[] { "Harmony Sequence", "harmonySequence" },
        new string[] { "Simon Scrambles", "simonScrambles" },
        new string[] { "Unfair Cipher", "unfairCipher" },
        new string[] { "Melody Sequencer", "melodySequencer" },
        new string[] { "Colorful Insanity", "ColorfulInsanity" },
        new string[] { "Gadgetron Vendor", "lgndGadgetronVendor" },
        new string[] { "Left and Right", "leftandRight" },
        new string[] { "Passport Control", "passportControl" },
        new string[] { "Wingdings", "needyWingdings" },
        new string[] { "The Hexabutton", "hexabutton" },
        new string[] { "The Plunger", "needyPlunger" },
        new string[] { "Genetic Sequence", "geneticSequence" },
        new string[] { "Micro-Modules", "KritMicroModules" },
        new string[] { "Elder Futhark", "elderFuthark" },
        new string[] { "Module Maze", "ModuleMaze" },
        new string[] { "Tasha Squeals", "tashaSqueals" },
        new string[] { "Forget This", "forgetThis" },
        new string[] { "Digital Cipher", "digitalCipher" },
        new string[] { "Burger Alarm", "burgerAlarm" },
        new string[] { "Draw", "draw" },
        new string[] { "Grocery Store", "groceryStore" },
        new string[] { "Subscribe to Pewdiepie", "subscribeToPewdiepie" },
        new string[] { "Lombax Cubes", "lgndLombaxCubes" },
        new string[] { "Mega Man 2", "megaMan2" },
        new string[] { "Purgatory", "PurgatoryModule" },
        new string[] { "The Stare", "StareModule" },
        new string[] { "Graphic Memory", "graphicMemory" },
        new string[] { "Quiz Buzz", "quizBuzz" },
        new string[] { "Wavetapping", "Wavetapping" },
        new string[] { "The Hypercube", "TheHypercubeModule" },
        new string[] { "Speak English", "speakEnglish" },
        new string[] { "Seven Wires", "sevenWires" },
        new string[] { "Stack'em", "stackem" },
        new string[] { "Colored Keys", "lgndColoredKeys" },
        new string[] { "The Troll", "troll" },
        new string[] { "Planets", "planets" },
        new string[] { "The Necronomicon", "necronomicon" },
        new string[] { "Four-Card Monte", "Krit4CardMonte" },
        new string[] { "aa", "aa" },
        new string[] { "Alpha", "lgndAlpha" },
        new string[] { "Digit String", "digitString" },
        new string[] { "The Giant's Drink", "giantsDrink" },
        new string[] { "Hidden Colors", "lgndHiddenColors" },
        new string[] { "Snap!", "lgndSnap" },
        new string[] { "Colour Code", "colourcode" },
        new string[] { "Brush Strokes", "brushStrokes" },
        new string[] { "Vexillology", "vexillology" },
        new string[] { "Odd One Out", "OddOneOutModule" },
        new string[] { "Mazematics", "mazematics" },
        new string[] { "The Triangle Button", "theTriangleButton" },
        new string[] { "Equations X", "equationsXModule" },
        new string[] { "Maze³", "maze3" },
        new string[] { "Gryphons", "gryphons" },
        new string[] { "Arithmelogic", "arithmelogic" },
        new string[] { "Roman Art", "romanArtModule" },
        new string[] { "Faulty Sink", "FaultySink" },
        new string[] { "Simon Stops", "simonStops" },
        new string[] { "Morse Buttons", "morseButtons" },
        new string[] { "Terraria Quiz", "lgndTerrariaQuiz" },
        new string[] { "Baba Is Who?", "babaIsWho" },
        new string[] { "Daylight Directions", "daylightDirections" },
        new string[] { "Modulus Manipulation", "modulusManipulation" },
        new string[] { "Risky Wires", "riskyWires" },
        new string[] { "Simon Stores", "simonStores" },
        new string[] { "Triangle Buttons", "triangleButtons" },
        new string[] { "Cryptic Password", "CrypticPassword" },
        new string[] { "Stained Glass", "stainedGlass" },
        new string[] { "The Block", "theBlock" },
        new string[] { "Bamboozling Button", "bamboozlingButton" },
        new string[] { "Insane Talk", "insanetalk" },
        new string[] { "Transmitted Morse", "transmittedMorseModule" },
        new string[] { "A Mistake", "MistakeModule" },
        new string[] { "Green Arrows", "greenArrowsModule" },
        new string[] { "Red Arrows", "redArrowsModule" },
        new string[] { "Encrypted Equations", "EncryptedEquationsModule" },
        new string[] { "Encrypted Values", "EncryptedValuesModule" },
        new string[] { "Yellow Arrows", "yellowArrowsModule" },
        new string[] { "Forget Them All", "forgetThemAll" },
        new string[] { "Ordered Keys", "orderedKeys" },
        new string[] { "Blue Arrows", "blueArrowsModule" },
        new string[] { "Sticky Notes", "stickyNotes" },
        new string[] { "Hyperactive Numbers", "lgndHyperactiveNumbers" },
        new string[] { "Orange Arrows", "orangeArrowsModule" },
        new string[] { "Unordered Keys", "unorderedKeys" },
        new string[] { "Reordered Keys", "reorderedKeys" },
        new string[] { "Button Grid", "buttonGrid" },
        new string[] { "Find The Date", "DateFinder" },
        new string[] { "Misordered Keys", "misorderedKeys" },
        new string[] { "The Matrix", "matrix" },
        new string[] { "Purple Arrows", "purpleArrowsModule" },
        new string[] { "Bordered Keys", "borderedKeys" },
        new string[] { "The Dealmaker", "thedealmaker" },
        new string[] { "Seven Deadly Sins", "sevenDeadlySins" },
        new string[] { "The Ultracube", "TheUltracubeModule" },
        new string[] { "Symbolic Colouring", "symbolicColouring" },
        new string[] { "Recorded Keys", "recordedKeys" },
        new string[] { "The Deck of Many Things", "deckOfManyThings" },
        new string[] { "Character Codes", "characterCodes" },
        new string[] { "Disordered Keys", "disorderedKeys" },
        new string[] { "Raiding Temples", "raidingTemples" },
        new string[] { "Bomb Diffusal", "bombDiffusal" },
        new string[] { "Pong", "NeedyPong" },
        new string[] { "Tallordered Keys", "tallorderedKeys" },
        new string[] { "Cruel Ten Seconds", "cruel10sec" },
        new string[] { "Ten Seconds", "10seconds" },
        new string[] { "Boolean Keypad", "BooleanKeypad" },
        new string[] { "Calculus", "calcModule" },
        new string[] { "Double Expert", "doubleExpert" },
        new string[] { "Pictionary", "pictionaryModule" },
        new string[] { "Toon Enough", "toonEnough" },
        new string[] { "Qwirkle", "qwirkle" },
        new string[] { "Antichamber", "antichamber" },
        new string[] { "Simon Simons", "simonSimons" },
        new string[] { "Constellations", "constellations" },
        new string[] { "Forget Enigma", "forgetEnigma" },
        new string[] { "Lucky Dice", "luckyDice" },
        new string[] { "Cruel Digital Root", "cruelDigitalRootModule" },
        new string[] { "Prime Checker", "PrimeChecker" },
        new string[] { "Faulty Digital Root", "faultyDigitalRootModule" },
        new string[] { "The Crafting Table", "needycrafting" },
        new string[] { "Boot Too Big", "bootTooBig" },
        new string[] { "Vigenère Cipher", "vigenereCipher" },
        new string[] { "Langton's Ant", "langtonAnt" },
        new string[] { "Old Fogey", "oldFogey" },
        new string[] { "Insanagrams", "insanagrams" },
        new string[] { "Treasure Hunt", "treasureHunt" },
        new string[] { "Snakes and Ladders", "snakesAndLadders" },
        new string[] { "Module Movements", "moduleMovements" },
        new string[] { "Bamboozled Again", "bamboozledAgain" },
        new string[] { "Roman Numerals", "romanNumeralsModule" },
        new string[] { "Safety Square", "safetySquare" },
        new string[] { "Colo(u)r Talk", "colourTalk" },
        new string[] { "Annoying Arrows", "lgndAnnoyingArrows" },
        new string[] { "Block Stacks", "blockStacks" },
        new string[] { "Boolean Wires", "booleanWires" },
        new string[] { "Double Arrows", "doubleArrows" },
        new string[] { "Caesar Cycle", "caesarCycle" },
        new string[] { "Partial Derivatives", "partialDerivatives" },
        new string[] { "Vectors", "vectorsModule" },
        new string[] { "Forget Us Not", "forgetUsNot" },
        new string[] { "Needy Piano", "needyPiano" },
        new string[] { "Affine Cycle", "affineCycle" },
        new string[] { "Pigpen Cycle", "pigpenCycle" },
        new string[] { "Flower Patch", "flowerPatch" },
        new string[] { "Playfair Cycle", "playfairCycle" },
        new string[] { "Jumble Cycle", "jumbleCycle" },
        new string[] { "Alpha-Bits", "alphaBits" },
        new string[] { "Forget Perspective", "qkForgetPerspective" },
        new string[] { "Organization", "organizationModule" },
        new string[] { "Jack Attack", "jackAttack" },
        new string[] { "Binary", "Binary" },
        new string[] { "Hill Cycle", "hillCycle" },
        new string[] { "Ultimate Cycle", "ultimateCycle" },
        new string[] { "Chord Progressions", "chordProgressions" },
        new string[] { "Matchematics", "matchematics" },
        new string[] { "Bob Barks", "ksmBobBarks" },
        new string[] { "Simon's On First", "simonsOnFirst" },
        new string[] { "Forget Me Now", "ForgetMeNow" },
        new string[] { "Weird Al Yankovic", "weirdAlYankovic" },
        new string[] { "Simon Selects", "simonSelectsModule" },
        new string[] { "Cryptic Cycle", "crypticCycle" },
        new string[] { "Simon Literally Says", "ksmSimonLitSays" },
        new string[] { "The Witness", "thewitness" },
        new string[] { "Bone Apple Tea", "boneAppleTea" },
        new string[] { "Masyu", "masyuModule" },
        new string[] { "Robot Programming", "robotProgramming" },
        new string[] { "Hold Ups", "KritHoldUps" },
        new string[] { "Red Cipher", "redCipher" },
        new string[] { "A-maze-ing Buttons", "ksmAmazeingButtons" },
        new string[] { "Flash Memory", "FlashMemory" },
        new string[] { "Desert Bus", "desertBus" },
        new string[] { "Common Sense", "commonSense" },
        new string[] { "Orange Cipher", "orangeCipher" },
        new string[] { "Needy Flower Mash", "R4YNeedyFlowerMash" },
        new string[] { "The Very Annoying Button", "veryAnnoyingButton" },
        new string[] { "Unown Cipher", "UnownCipher" },
        new string[] { "TetraVex", "ksmTetraVex" },
        new string[] { "Meter", "meter" },
        new string[] { "The Modkit", "modkit" },
        new string[] { "Timing is Everything", "timingIsEverything" },
        new string[] { "Bamboozling Button Grid", "bamboozlingButtonGrid" },
        new string[] { "Fruits", "fruits" },
        new string[] { "The Rule", "theRule" },
        new string[] { "Footnotes", "footnotes" },
        new string[] { "Lousy Chess", "lousyChess" },
        new string[] { "Module Listening", "moduleListening" },
        new string[] { "Garfield Kart", "garfieldKart" },
        new string[] { "Green Cipher", "greenCipher" },
        new string[] { "Kooky Keypad", "kookyKeypadModule" },
        new string[] { "Yellow Cipher", "yellowCipher" },
        new string[] { "RGB Maze", "rgbMaze" },
        new string[] { "Blue Cipher", "blueCipher" },
        new string[] { "The Legendre Symbol", "legendreSymbol" },
        new string[] { "Forget Me Later", "forgetMeLater" },
        new string[] { "Keypad Lock", "keypadLock" },
        new string[] { "Heraldry", "heraldry" },
        new string[] { "Faulty RGB Maze", "faultyrgbMaze" },
        new string[] { "Indigo Cipher", "indigoCipher" },
        new string[] { "Violet Cipher", "violetCipher" },
        new string[] { "Chinese Counting", "chineseCounting" },
        new string[] { "Color Addition", "colorAddition" },
        new string[] { "Encryption Bingo", "encryptionBingo" },
        new string[] { "Tower of Hanoi", "towerOfHanoi" },
        new string[] { "Keypad Combinations", "keypadCombinations" },
        new string[] { "Kanji", "KanjiModule" },
        new string[] { "UltraStores", "UltraStores" },
        new string[] { "Geometry Dash", "geometryDashModule" },
        new string[] { "Ternary Converter", "qkTernaryConverter" },
        new string[] { "N&Ms", "NandMs" },
        new string[] { "Eight Pages", "lgndEightPages" },
        new string[] { "The Colored Maze", "coloredMaze" },
        new string[] { "White Cipher", "whiteCipher" },
        new string[] { "Gray Cipher", "grayCipher" },
        new string[] { "Black Cipher", "blackCipher" },
        new string[] { "The Hyperlink", "hyperlink" },
        new string[] { "Loopover", "loopover" },
        new string[] { "Corners", "CornersModule" },
        new string[] { "Divisible Numbers", "divisibleNumbers" },
        new string[] { "The High Score", "ksmHighScore" },
        new string[] { "Ingredients", "ingredients" },
        new string[] { "Cruel Boolean Maze", "boolMazeCruel" },
        new string[] { "Intervals", "intervals" },
        new string[] { "Jenga", "jenga" },
        new string[] { "Cheep Checkout", "cheepCheckout" },
        new string[] { "Spelling Bee", "spellingBee" },
        new string[] { "Memorable Buttons", "memorableButtons" },
        new string[] { "Thinking Wires", "thinkingWiresModule" },
        new string[] { "Object Shows", "objectShows" },
        new string[] { "Seven Choose Four", "sevenChooseFour" },
        new string[] { "Lunchtime", "lunchtime" },
        new string[] { "Natures", "mcdNatures" },
        new string[] { "Neutrinos", "neutrinos" },
        new string[] { "Scavenger Hunt", "scavengerHunt" },
        new string[] { "Polygons", "polygons" },
        new string[] { "Ultimate Cipher", "ultimateCipher" },
        new string[] { "Codenames", "codenames" },
        new string[] { "Odd Mod Out", "lgndOddModOut" },
        new string[] { "Blinkstop", "blinkstopModule" },
        new string[] { "Logic Statement", "logicStatement" },
        new string[] { "Ultimate Custom Night", "qkUCN" },
        new string[] { "Hinges", "hinges" },
        new string[] { "Answering Can Be Fun", "AnsweringCanBeFun" },
        new string[] { "BuzzFizz", "buzzfizz" },
        new string[] { "egg", "bigegg" },
        new string[] { "Forget It Not", "forgetItNot" },
        new string[] { "Time Accumulation", "timeAccumulation" },
        new string[] { "Rainbow Arrows", "ksmRainbowArrows" },
        new string[] { "Digital Dials", "digitalDials" },
        new string[] { "Multicolored Switches", "R4YMultiColoredSwitches" },
        new string[] { "Time Signatures", "timeSignatures" },
        new string[] { "Hereditary Base Notation", "hereditaryBaseNotationModule" },
        new string[] { "Passcodes", "xtrpasscodes" },
        new string[] { "Lines of Code", "linesOfCode" },
        new string[] { "The cRule", "the_cRule" },
        new string[] { "Colorful Dials", "colorfulDials" },
        new string[] { "Encrypted Dice", "EncryptedDice" },
        new string[] { "Prime Encryption", "primeEncryption" },
        new string[] { "Naughty or Nice", "lgndNaughtyOrNice" },
        new string[] { "Following Orders", "FollowingOrders" },
        new string[] { "Binary Grid", "binaryGrid" },
        new string[] { "Cruel Keypads", "CruelKeypads" },
        new string[] { "Matrices", "MatrixQuiz" },
        new string[] { "The Black Page", "TheBlackPage" },
        new string[] { "Simon Forgets", "simonForgets" },
        new string[] { "Greek Letter Grid", "greekLetterGrid" },
        new string[] { "Bamboozling Time Keeper", "bamboozlingTimeKeeper" },
        new string[] { "Scalar Dials", "scalarDials" },
        new string[] { "Keywords", "xtrkeywords" },
        new string[] { "The World's Largest Button", "WorldsLargestButton" },
        new string[] { "State of Aggregation", "stateOfAggregation" },
        new string[] { "Dreamcipher", "ksmDreamcipher" },
        new string[] { "Brainf---", "brainf" },
        new string[] { "Boozleglyph Identification", "boozleglyphIdentification" },
        new string[] { "Echolocation", "echolocation" },
        new string[] { "Hyperneedy", "hyperneedy" },
        new string[] { "Patience", "patience" },
        new string[] { "Rotating Squares", "rotatingSquares" },
        new string[] { "Boxing", "boxing" },
        new string[] { "Topsy Turvy", "topsyTurvy" },
        new string[] { "Railway Cargo Loading", "RailwayCargoLoading" },
        new string[] { "ASCII Art", "asciiArt" },
        new string[] { "Conditional Buttons", "conditionalButtons" },
        new string[] { "Semamorse", "semamorse" },
        new string[] { "Hide and Seek", "hideAndSeek" },
        new string[] { "Symbolic Tasha", "symbolicTasha" },
        new string[] { "Alphabetical Ruling", "alphabeticalRuling" },
        new string[] { "Microphone", "Microphone" },
        new string[] { "Widdershins", "widdershins" },
        new string[] { "Dimension Disruption", "dimensionDisruption" },
        new string[] { "Lockpick Maze", "KritLockpickMaze" },
        new string[] { "V", "V" },
        new string[] { "A Message", "AMessage" },
        new string[] { "Alliances", "alliances" },
        new string[] { "Silhouettes", "silhouettes" },
        new string[] { "Dungeon", "dungeon" },
        new string[] { "Unicode", "UnicodeModule" },
        new string[] { "Password Generator", "pwGenerator" },
        new string[] { "Baccarat", "baccarat" },
        new string[] { "Guess Who?", "GuessWho" },
        new string[] { "Alphabetize", "Alphabetize" },
        new string[] { "Reverse Alphabetize", "ReverseAlphabetize" },
        new string[] { "Gatekeeper", "gatekeeper" },
        new string[] { "Light Bulbs", "LightBulbs" },
        new string[] { "Five Letter Words", "FiveLetterWords" },
        new string[] { "Settlers of KTaNE", "SettlersOfKTaNE" },
        new string[] { "The Hidden Value", "theHiddenValue" },
        new string[] { "Blue", "BlueNeedy" },
        new string[] { "Red", "RedNeedy" },
        new string[] { "Directional Button", "directionalButton" },
        new string[] { "Misery Squares", "SquaresOfMisery" },
        new string[] { "The Simpleton", "SimpleButton" },
        new string[] { "Dungeon 2nd Floor", "dungeon2" },
        new string[] { "Sequences", "sequencesModule" },
        new string[] { "Vcrcs", "VCRCS" },
        new string[] { "Wire Ordering", "kataWireOrdering" },
        new string[] { "Quaternions", "quaternions" },
        new string[] { "Abstract Sequences", "abstractSequences" },
        new string[] { "osu!", "osu" },
        new string[] { "Shifting Maze", "MazeShifting" },
        new string[] { "Art Appreciation", "AppreciateArt" },
        new string[] { "Placeholder Talk", "placeholderTalk" },
        new string[] { "Role Reversal", "roleReversal" },
        new string[] { "Sorting", "sorting" },
        new string[] { "Pattern Lock", "patternLock" },
        new string[] { "Shell Game", "shellGame" },
        new string[] { "Cheat Checkout", "kataCheatCheckout" },
        new string[] { "Minecraft Cipher", "minecraftCipher" },
        new string[] { "Quick Arithmetic", "QuickArithmetic" },
        new string[] { "Forget The Colors", "ForgetTheColors" },
        new string[] { "The Samsung", "theSamsung" },
        new string[] { "Etterna", "etterna" },
        new string[] { "Cruel Garfield Kart", "CruelGarfieldKart" },
        new string[] { "Recolored Switches", "R4YRecoloredSwitches" },
        new string[] { "Reverse Polish Notation", "revPolNot" },
        new string[] { "Snowflakes", "snowflakes" },
        new string[] { "Exoplanets", "exoplanets" },
        new string[] { "Faulty Seven Segment Displays", "faulty7SegmentDisplays" },
        new string[] { "Forget Infinity", "forgetInfinity" },
        new string[] { "Simon Stages", "simonStages" },
        new string[] { "Malfunctions", "malfunctions" },
        new string[] { "Roger", "roger" },
        new string[] { "Stock Images", "StockImages" },
        new string[] { "Minecraft Parody", "minecraftParody" },
        new string[] { "Minecraft Survival", "kataMinecraftSurvival" },
        new string[] { "NumberWang", "kikiNumberWang" },
        new string[] { "Shuffled Strings", "shuffledStrings" },
        new string[] { "Fencing", "fencing" },
        new string[] { "RPS Judging", "RPSJudging" },
        new string[] { "Strike/Solve", "strikeSolve" },
        new string[] { "The Twin", "TheTwinModule" },
        new string[] { "Uncolored Switches", "R4YUncoloredSwitches" },
        new string[] { "Name Changer", "nameChanger" },
        new string[] { "Just Numbers", "JustNumbersModule" },
        new string[] { "Flag Identification", "needyFlagIdentification" },
        new string[] { "Lying Indicators", "lyingIndicators" },
        new string[] { "Training Text", "TrainingText" },
        new string[] { "Caesar's Maths", "caesarsMaths" },
        new string[] { "Wonder Cipher", "WonderCipher" },
        new string[] { "Random Access Memory", "RAM" },
        new string[] { "Triamonds", "triamonds" },
        new string[] { "Button Order", "buttonOrder" },
        new string[] { "Stars", "stars" },
        new string[] { "Elder Password", "elderPassword" },
        new string[] { "Iconic", "iconic" },
        new string[] { "Switching Maze", "MazeSwitching" },
        new string[] { "Ladder Lottery", "ladderLottery" },
        new string[] { "Mystery Module", "mysterymodule" },
        new string[] { "Co-op Harmony Sequence", "coopharmonySequence" },
        new string[] { "Arrow Talk", "ArrowTalk" },
        new string[] { "BoozleTalk", "BoozleTalk" },
        new string[] { "Crazy Talk With A K", "CrazyTalkWithAK" },
        new string[] { "Deck Creating", "kataDeckCreating" },
        new string[] { "Jaden Smith Talk", "JadenSmithTalk" },
        new string[] { "KayMazey Talk", "KMazeyTalk" },
        new string[] { "Kilo Talk", "KiloTalk" },
        new string[] { "Quote Crazy Talk End Quote", "QuoteCrazyTalkEndQuote" },
        new string[] { "Standard Crazy Talk", "StandardCrazyTalk" },
        new string[] { "Siffron", "siffron" },
        new string[] { "Audio Morse", "lgndAudioMorse" },
        new string[] { "Palindromes", "palindromes" },
        new string[] { "Pow", "powModule" },
        new string[] { "Badugi", "ksmBadugi" },
        new string[] { "Chicken Nuggets", "ChickenNuggets" },
        new string[] { "Type Racer", "typeRacer" },
        new string[] { "Masher The Bottun", "masherTheBottun" },
        new string[] { "Negativity", "Negativity" },
        new string[] { "Spot the Difference", "SpotTheDifference" },
        new string[] { "Tetriamonds", "tetriamonds" },
        new string[] { "M&Ns", "MandNs" },
        new string[] { "Yes and No", "yesandno" },
        new string[] { "Goofy's Game", "goofysgame" },
        new string[] { "Integer Trees", "IntegerTrees" },
        new string[] { "Plant Identification", "PlantIdentification" },
        new string[] { "Module Rick", "ModuleRick" },
        new string[] { "Earthbound", "EarthboundModule" },
        new string[] { "Pickup Identification", "PickupIdentification" },
        new string[] { "Life Iteration", "LifeIteration" },
        new string[] { "Accelerando", "accelerando" },
        new string[] { "Encrypted Hangman", "encryptedHangman" },
        new string[] { "Thread the Needle", "threadTheNeedle" },
        new string[] { "Color Braille", "ColorBrailleModule" },
        new string[] { "Reaction", "xtrreaction" },
        new string[] { "The Heart", "TheHeart" },
        new string[] { "Remote Math", "remotemath" },
        new string[] { "Reflex", "lgndReflex" },
        new string[] { "Password Destroyer", "pwDestroyer" },
        new string[] { "hexOS", "hexOS" },
        new string[] { "Multitask", "multitask" },
        new string[] { "Typing Tutor", "needyTypingTutor" },
        new string[] { "Brawler Database", "brawlerDatabaseModule" },
        new string[] { "Kyudoku", "kyudoku" },
        new string[] { "Simon Stashes", "simonStashes" },
        new string[] { "Shortcuts", "shortcuts" },
        new string[] { "More Code", "MoreCode" },
        new string[] { "OmegaForget", "omegaForget" },
        new string[] { "Basic Morse", "BasicMorse" },
        new string[] { "Bloxx", "bloxx" },
        new string[] { "Dictation", "Dictation" },
        new string[] { "Kugelblitz", "kugelblitz" },
        new string[] { "Mental Math", "MentalMath" },
        new string[] { "Needy Game of Life", "gameOfLifeNeedy" },
        new string[] { "Emotiguy Identification", "EmotiguyIdentification" },
        new string[] { "IPA", "ipa" },
        new string[] { "DACH Maze", "DACH" },
        new string[] { "Dumb Waiters", "dumbWaiters" },
        new string[] { "Jailbreak", "Jailbreak" },
        new string[] { "NeeDeez Nuts", "NeeDeezNuts" },
        new string[] { "Birthdays", "birthdays" },
        new string[] { "Match 'em", "matchem" },
        new string[] { "Gnomish Puzzle", "qkGnomishPuzzle" },
        new string[] { "Navinums", "navinums" },
        new string[] { "A>N<D", "ANDmodule" },
        new string[] { "Bridges", "bridges" },
        new string[] { "RGB Logic", "rgbLogic" },
        new string[] { "Juxtacolored Squares", "JuxtacoloredSquaresModule" },
        new string[] { "Shifted Maze", "shiftedMaze" },
        new string[] { "Amnesia", "Amnesia" },
        new string[] { "The Missing Letter", "theMissingLetter" },
        new string[] { "Wolf, Goat, and Cabbage", "wolfGoatCabbageModule" },
        new string[] { "Plug-Ins", "plugins" },
        new string[] { "Synesthesia", "synesthesia" },
        new string[] { "English Entries", "EnglishEntries" },
        new string[] { "The Cruel Duck", "theCruelDuck" },
        new string[] { "The Duck", "theDuck" },
        new string[] { "Identifying Soulless", "identifyingSoulless" },
        new string[] { "Factoring", "factoring" },
        new string[] { "Ultimate Tic Tac Toe", "ultimateTicTacToe" },
        new string[] { "Lyrical Nonsense", "lyricalNonsense" },
        new string[] { "NOT NOT", "notnot" },
        new string[] { "Puzzword", "PuzzwordModule" },
        new string[] { "RGB Sequences", "RGBSequences" },
        new string[] { "Deaf Alley", "deafAlleyModule" },
        new string[] { "int##", "int##" },
        new string[] { "Repo Selector", "qkRepoSelector" },
        new string[] { "Blind Arrows", "blindArrows" },
        new string[] { "D-CODE", "xelDcode" },
        new string[] { "RGB Arithmetic", "rgbArithmetic" },
        new string[] { "Sound Design", "soundDesign" },
        new string[] { "Fifteen", "fifteen" },
        new string[] { "Rapid Subtraction", "rapidSubtraction" },
        new string[] { "Don't Touch Anything", "dontTouchAnything" },
        new string[] { "Pixel Cipher", "pixelcipher" },
        new string[] { "The Great Void", "greatVoid" },
        new string[] { "Negation", "xelNegation" },
        new string[] { "Prime Time", "primeTime" },
        new string[] { "The Calculator", "TheCalculator" },
        new string[] { "ASCII Maze", "asciiMaze" },
        new string[] { "SixTen", "sixten" },
        new string[] { "Ultralogic", "Ultralogic" },
        new string[] { "Busy Beaver", "busyBeaver" },
        new string[] { "Spangled Stars", "spangledStars" },
        new string[] { "Digital Clock", "digitalClock" },
        new string[] { "Assembly Code", "assemblyCode" },
        new string[] { "Cruel Match 'em", "matchemcruel" },
        new string[] { "Simon's Ultimate Showdown", "simonsUltimateShowdownModule" },
        new string[] { "Boomdas", "boomdas" },
        new string[] { "Chinese Strokes", "zhStrokes" },
        new string[] { "Color Numbers", "colorNumbers" },
        new string[] { "Needlessly Complicated Button", "needlesslyComplicatedButton" },
        new string[] { "Chalices", "Chalices" },
        new string[] { "Pixel Art", "PixelArt" },
        new string[] { "Reversed Edgework", "ReversedEdgework" },
        new string[] { "Faulty Accelerando", "faultyAccelerandoModule" },
        new string[] { "Broken Binary", "BrokenBinary" },
        new string[] { "Connected Monitors", "ConnectedMonitorsModule" },
        new string[] { "Cruel Binary", "CruelBinary" },
        new string[] { "Faulty Binary", "FaultyBinary" },
        new string[] { "Increasing Indices", "increasingIndices" },
        new string[] { "Pitch Perfect", "pitchPerfect" },
        new string[] { "Color-Cycle Button", "colorCycleButton" },
        new string[] { "D-CRYPT", "xelDcrypt" },
        new string[] { "ReGret-B Filtering", "regretbFiltering" },
        new string[] { "Tell Me When", "GSTellMeWhen" },
        new string[] { "Totally Accurate Minecraft Simulator", "tams" },
        new string[] { "Alien Filing Colors", "AlienModule" },
        new string[] { "Entry Number Four", "GSEntryNumberFour" },
        new string[] { "The Kanye Encounter", "TheKanyeEncounter" },
        new string[] { "D-CIPHER", "xelDcipher" },
        new string[] { "Color One Two", "colorOneTwo" },
        new string[] { "Brown Bricks", "xelBrownBricks" },
        new string[] { "Burnout", "kataBurnout" },
        new string[] { "Spelling Buzzed", "SpellingBuzzed" },
        new string[] { "Toolmods", "toolmods" },
        new string[] { "Chinese Zodiac", "xelChineseZodiac" },
        new string[] { "Mystic Maze", "mysticmaze" },
        new string[] { "Duck, Duck, Goose", "DUCKDUCKGOOSE" },
        new string[] { "Four Lights", "fourLights" },
        new string[] { "One Links To All", "oneLinksToAllModule" },
        new string[] { "Toolneedy", "toolneedy" },
        new string[] { "Working Title", "workingTitle" },
        new string[] { "Rules", "Rules" },
        new string[] { "Tenpins", "tenpins" },
        new string[] { "Double Listening", "doubleListening" },
        new string[] { "Unfair's Revenge", "unfairsRevenge" },
        new string[] { "Unfair's Cruel Revenge", "unfairsRevengeCruel" },
        new string[] { "Wack Game of Life", "wackGameOfLife" },
        new string[] { "Golf", "golf" },
        new string[] { "Mindlock", "mindlock" },
        new string[] { "Literally Nothing", "literallyNothing" },
        new string[] { "Regular Hexpressions", "RegularHexpressions" },
        new string[] { "Censorship", "Censorship" },
        new string[] { "Colored Buttons", "ColoredButtons" },
        new string[] { "Mechanus Cipher", "mechanusCipher" },
        new string[] { "The Pentabutton", "GSPentabutton" },
        new string[] { "Breaktime", "breaktime" },
        new string[] { "Digisibility", "digisibility" },
        new string[] { "Kim's Game", "KimsGame" },
        new string[] { "Mazery", "Mazery" },
        new string[] { "Space Invaders Extreme", "GSSpaceInvadersExtreme" },
        new string[] { "Popufur", "popufur" },
        new string[] { "Three Cryptic Steps", "ThreeCrypticSteps" },
        new string[] { "Space", "xelSpace" },
        new string[] { "Tech Support", "TechSupport" },
        new string[] { "Metamem", "metamem" },
        new string[] { "M&Ms", "MandMs" },
        new string[] { "The Console", "console" },
        new string[] { "Pocket Planes", "pocketPlanesModule" },
        new string[] { "Bridge", "bridge" },
        new string[] { "Beans", "beans" },
        new string[] { "Beanboozled Again", "beanboozledAgain" },
        new string[] { "Cool Beans", "coolBeans" },
        new string[] { "Jellybeans", "jellybeans" },
        new string[] { "Long Beans", "longBeans" },
        new string[] { "Rotten Beans", "rottenBeans" },
        new string[] { "Broken Karaoke", "xelBrokenKaraoke" },
        new string[] { "Butterflies", "xelButterflies" },
        new string[] { "The Dials", "TheDials" },
        new string[] { "Chamber No. 5", "ChamberNoFive" },
        new string[] { "Silenced Simon", "SilencedSimon" },
        new string[] { "Teal Arrows", "tealArrowsModule" },
        new string[] { "Frankenstein's Indicator", "frankensteinsIndicator" },
        new string[] { "Keep Clicking", "keepClicking" },
        new string[] { "Alphabet Tiles", "AlphabetTiles" },
        new string[] { "Sea Bear Attacks", "seaBearAttacksModule" },
        new string[] { "Devilish Eggs", "devilishEggs" },
        new string[] { "Double Pitch", "DoublePitch" },
        new string[] { "Literally Crying", "literallyCrying" },
        new string[] { "h", "Averageh" },
        new string[] { "Rune Match I", "runeMatchI" },
        new string[] { "Rune Match II", "runeMatchII" },
        new string[] { "Rune Match III", "runeMatchIII" },
        new string[] { "Ars Goetia Identification", "arsGoetiaIdentification" },
        new string[] { "Iñupiaq Numerals", "inupiaqNumerals" },
        new string[] { "Quick Time Events", "xelQuickTimeEvents" },
        new string[] { "The Bioscanner", "TheBioscanner" },
        new string[] { "Pixel Number Base", "PixelNumberBase" },
        new string[] { "Gradually Watermelon", "graduallyWatermelon" },
        new string[] { "Silo Authorization", "siloAuthorization" },
        new string[] { "Digital Grid", "digitalGrid" },
        new string[] { "Even Or Odd", "evenOrOdd" },
        new string[] { "Higher Or Lower", "HigherOrLower" },
        new string[] { "Logical Operators", "logicalOperators" },
        new string[] { "Mastermind Restricted", "mastermindRestricted" },
        new string[] { "Reformed Role Reversal", "ReformedRoleReversal" },
        new string[] { "Whiteout", "whiteout" },
        new string[] { "Cell Lab", "cellLab" },
        new string[] { "Gettin' Funky", "gettinFunkyModule" },
        new string[] { "N&Ns", "NandNs" },
        new string[] { "Color Hexagons", "colorHexagons" },
        new string[] { "Lights On", "lightson" },
        new string[] { "Commuting", "commuting" },
        new string[] { "Look and Say", "LookAndSay" },
        new string[] { "Symmetries Of A Square", "xelSymmetriesOfASquare" },
        new string[] { "Currents", "Currents" },
        new string[] { "Partitions", "partitions" },
        new string[] { "Cruel Stars", "cruelStars" },
        new string[] { "Telepathy", "Telepathy" },
        new string[] { "Button Messer", "qkButtonMesser" },
        new string[] { "Forget Any Color", "ForgetAnyColor" },
        new string[] { "Nomai", "nomai" },
        new string[] { "Taco Tuesday", "tacoTuesday" },
        new string[] { "Melodic Message", "melodicMessage" },
        new string[] { "Table Madness", "TableMadness" },
        new string[] { "Colour Catch", "colourCatch" },
        new string[] { "Sugar Skulls", "sugarSkulls" },
        new string[] { "Cosmic", "CosmicModule" },
        new string[] { "Mislocation", "mislocation" },
        new string[] { "Semabols", "xelSemabols" },
        new string[] { "Musher the Batten", "musherTheBatten" },
        new string[] { "Simon Smiles", "SimonSmiles" },
        new string[] { "Tribal Council", "TribalCouncil" },
        new string[] { "Outrageous", "outrageous" },
        new string[] { "Faulty Chinese Counting", "faultyChineseCounting" },
        new string[] { "Press The Shape", "pressTheShape" },
        new string[] { "Baybayin Words", "BaybayinWords" },
        new string[] { "OmegaDestroyer", "omegaDestroyer" },
        new string[] { "Atbash Cipher", "AtbashCipher" },
        new string[] { "Going Backwards", "GoingBackwards" },
        new string[] { "Blue Hexabuttons", "blueHexabuttons" },
        new string[] { "Green Hexabuttons", "greenHexabuttons" },
        new string[] { "Numbered Buttons", "numberedButtonsModule" },
        new string[] { "Orange Hexabuttons", "orangeHexabuttons" },
        new string[] { "Purple Hexabuttons", "purpleHexabuttons" },
        new string[] { "Red Hexabuttons", "redHexabuttons" },
        new string[] { "Venn Diagrams", "vennDiagram" },
        new string[] { "White Hexabuttons", "whiteHexabuttons" },
        new string[] { "Yellow Hexabuttons", "yellowHexabuttons" },
        new string[] { "Video Poker", "videoPoker" },
        new string[] { "Bottom Gear", "GSBottomGear" },
        new string[] { "Johnson Solids", "xelJohnsonSolids" },
        new string[] { "White Arrows", "WhiteArrows" },
        new string[] { "Keypad Directionality", "KeypadDirectionality" },
        new string[] { "Two Persuasive Buttons", "TwoPersuasiveButtons" },
        new string[] { "Letter Layers", "xelLetterLayers" },
        new string[] { "Towers", "Towers" },
        new string[] { "The Exploding Pen", "TheExplodingPen" },
        new string[] { "ReGrettaBle Relay", "regrettablerelay" },
        new string[] { "Snack Attack", "SnackAttack" },
        new string[] { "Security Council", "SecurityCouncil" },
        new string[] { "Jackbox.TV", "jackboxServerModule" },
        new string[] { "Musical Transposition", "MusicalTransposition" },
        new string[] { "Standard Button Masher", "standardButtonMasher" },
        new string[] { "The Furloid Jukebox", "xelFurloidJukebox" },
        new string[] { "The Close Button", "TheCloseButton" },
        new string[] { "Addition", "Addition" },
        new string[] { "B-Machine", "xelBMachine" },
        new string[] { "Saimoe Pad", "SaimoePad" },
        new string[] { "Updog", "Updog" },
        new string[] { "Quaver", "Quaver" },
        new string[] { "What's on Second", "WhatsOnSecond" },
        new string[] { "Another Keypad Module", "xelAnotherKeypadModule" },
        new string[] { "Think Fast", "GSThinkFast" },
        new string[] { "Rhythm Test", "rhythmTest" },
        new string[] { "Shoddy Chess", "ShoddyChessModule" },
        new string[] { "Bad Wording", "BadWording" },
        new string[] { "Floor Lights", "FloorLights" },
        new string[] { "Validation", "ValidationNeedy" },
        new string[] { "Etch-A-Sketch", "etchASketch" },
        new string[] { "Diophantine Equations", "DiophantineEquations" },
        new string[] { "Zener Cards", "kataZenerCards" },
        new string[] { "Rullo", "rullo" },
        new string[] { "Striped Keys", "kataStripedKeys" },
        new string[] { "Ternary Tiles", "GSTernaryTiles" },
        new string[] { "Black Arrows", "blackArrowsModule" },
        new string[] { "Coloured Arrows", "colouredArrowsModule" },
        new string[] { "Cruello", "cruello" },
        new string[] { "Flashing Arrows", "flashingArrowsModule" },
        new string[] { "Double Screen", "doubleScreenModule" },
        new string[] { "Forget Maze Not", "forgetMazeNot" },
        new string[] { "Tetris Sprint", "tetrisSprint" },
        new string[] { "eeB gnillepS", "eeBgnilleps" },
        new string[] { "The Sequencyclopedia", "TheSequencyclopedia" },
        new string[] { "Number Checker", "NumberChecker" },
        new string[] { "Pandemonium Cipher", "pandemoniumCipher" },
        new string[] { "Mineswapper", "mineswapper" },
        new string[] { "Phosphorescence", "Phosphorescence" },
        new string[] { "The Klaxon", "klaxon" },
        new string[] { "Valued Keys", "valuedKeysModule" },
        new string[] { "Numerical Knight Movement", "NumericalKnightMovement" },
        new string[] { "Bandboozled Again", "bandboozledAgain" },
        new string[] { "Ramboozled Again", "ramboozledAgain" },
        new string[] { "SpriteClub Betting Simulation", "SpriteClubBettingSimulation" },
        new string[] { "Hole in One", "HoleInOne" },
        new string[] { "Simon Subdivides", "simonSubdivides" },
        new string[] { "Audio Keypad", "AudioKeypad" },
        new string[] { "Back Buttons", "backButtonsModule" },
        new string[] { "Collapse", "collapseBasic" },
        new string[] { "Hexiom", "hexiomModule" },
        new string[] { "Bean Sprouts", "beanSprouts" },
        new string[] { "Big Bean", "bigBean" },
        new string[] { "Chilli Beans", "chilliBeans" },
        new string[] { "Fake Beans", "fakeBeans" },
        new string[] { "Kidney Beans", "kidneyBeans" },
        new string[] { "Saimoe Maze", "SaimoeMaze" },
        new string[] { "Bowling", "Bowling" },
        new string[] { "Quiplash", "QLModule" },
        new string[] { "Tell Me Why", "GSTellMeWhy" },
        new string[] { "DNA Mutation", "DNAMutation" },
        new string[] { "Entry Number One", "GSEntryNumberOne" },
        new string[] { "Linq", "Linq" },
        new string[] { "Sporadic Segments", "xelSporadicSegments" },
        new string[] { "Boob Tube", "boobTubeModule" },
        new string[] { "RGB Hypermaze", "rgbhypermaze" },
        new string[] { "AAAAA", "AAAAA" },
        new string[] { "Regular Sudoku", "RegularSudoku" },
        new string[] { "Drive-In Window", "DIWindow" },
        new string[] { "Polyrhythms", "polyrhythms" },
        new string[] { "The 12 Days of Christmas", "GSTwelveDaysOfChristmas" },
        new string[] { "X", "xModule" },
        new string[] { "Y", "yModule" },
        new string[] { "Rebooting M-OS", "RebootingM-Os" },
        new string[] { "The Xenocryst", "GSXenocryst" },
        new string[] { "Complexity", "complexity" },
        new string[] { "Stacked Sequences", "stackedSequences" },
        new string[] { "Small Circle", "smallCircle" },
        new string[] { "Fractal Maze", "fractalMaze" },
        new string[] { "Simon Stumbles", "simonStumbles" },
        new string[] { "Wild Side", "WildSide" },
        new string[] { "The Octadecayotton", "TheOctadecayotton" },
        new string[] { "Colored Letters", "ColoredLetters" },
        new string[] { "Bomb Corp. Filing", "BCFilingNeedy" },
        new string[] { "Forget's Ultimate Showdown", "ForgetsUltimateShowdownModule" },
        new string[] { "Kahoot!", "Kahoot" },
        new string[] { "Mii Identification", "miiIdentification" },
        new string[] { "Ultra Digital Root", "ultraDigitalRootModule" },
        new string[] { "Simon Swindles", "simonSwindles" },
        new string[] { "Next In Line", "NextInLine" },
        new string[] { "Functional Mapping", "functionalMapping" },
        new string[] { "Keypad Maze", "KeypadMaze" },
        new string[] { "Stable Time Signatures", "StableTimeSignatures" },
        new string[] { "Astrological", "Astrological" },
        new string[] { "Corridors", "GSCorridors" },
        new string[] { "XmORse Code", "xmorse" },
        new string[] { "Decay", "decay" },
        new string[] { "Free Password", "FreePassword" },
        new string[] { "Large Free Password", "LargeFreePassword" },
        new string[] { "Large Password", "LargeVanillaPassword" },
        new string[] { "The Burnt", "burnt" },
        new string[] { "Access Codes", "GSAccessCodes" },
        new string[] { "Cistercian Numbers", "xelCistercianNumbers" },
        new string[] { "Brown Cipher", "brownCipher" },
        new string[] { "Code Cracker", "CodeCracker" },
        new string[] { "Indentation", "Indentation" },
        new string[] { "One-Line", "oneLine" },
        new string[] { "Double Knob", "GSDoubleKnob" },
        new string[] { "Interpunct", "interpunct" },
        new string[] { "The Speaker", "theSpeaker" },
        new string[] { "Name Codes", "nameCodes" },
        new string[] { "The 1, 2, 3 Game", "TheOneTwoThreeGame" },
        new string[] { "Hold On", "ashHoldOn" },
        new string[] { "Keypad Magnified", "keypadMagnified" },
        new string[] { "Papa's Pizzeria", "papasPizzeria" },
        new string[] { "Diffusion", "diffusion" },
        new string[] { "Coffee Beans", "coffeeBeans" },
        new string[] { "Soy Beans", "soyBeans" },
        new string[] { "The Shaker", "shaker" },
        new string[] { "Ghost Movement", "ghostMovement" },
        new string[] { "Letter Grid", "LetterGrid" },
        new string[] { "Newline", "newline" },
        new string[] { "Amusement Parks", "amusementParks" },
        new string[] { "RSA Cipher", "RSACipher" },
        new string[] { "Screensaver", "NeedyScreensaver" },
        new string[] { "Transmission Transposition", "transmissionTransposition" },
        new string[] { "Icon Reveal", "IconReveal" },
        new string[] { "Literally Something", "literallySomething" },
        new string[] { "hexOrbits", "hexOrbits" },
        new string[] { "Solitaire Cipher", "solitaireCipher" },
        new string[] { "Matchmaker", "matchmaker" },
        new string[] { "Hearthur", "hearthur" },
        new string[] { "Ladders", "ladders" },
        new string[] { "Color Punch", "ColorPunch" },
        new string[] { "Decimation", "decimation" },
        new string[] { "Count to 69420", "countToSixtynineThousandFourHundredAndTwenty" },
        new string[] { "Mssngv Wls", "MssngvWls" },
        new string[] { "Coinage", "Coinage" },
        new string[] { "Emoticon Math", "emoticonMathModule" },
        new string[] { "Naming Conventions", "NamingConventions" },
        new string[] { "Netherite", "Netherite" },
        new string[] { "Identifrac", "identifrac" },
        new string[] { "Simon Supports", "simonSupports" },
        new string[] { "Cruel Colour Flash", "cruelColourFlash" },
        new string[] { "Factoring Maze", "factoringMaze" },
        new string[] { "Numpath", "numpath" },
        new string[] { "The Logan Parody Jukebox", "LoganJukebox" },
        new string[] { "Binary Buttons", "BinaryButtons" },
        new string[] { "The Alteran Trail", "alteranTrail" },
        new string[] { "The Assorted Arrangement", "TheAssortedArrangement" },
        new string[] { "Needy Wires", "TDSNeedyWires" },
        new string[] { "Pathfinder", "GSPathfinder" },
        new string[] { "Turn Four", "turnFour" },
        new string[] { "Llama, Llama, Alpaca", "llamaLlamaAlpaca" },
        new string[] { "nya~", "TDSNya" },
        new string[] { "Cruel Synesthesia", "cruelSynesthesia" },
        new string[] { "Voltorb Flip", "VoltorbFlip" },
        new string[] { "Dossier Modifier", "TDSDossierModifier" },
        new string[] { "Polygrid", "polygrid" },
        new string[] { "amogus", "TDSAmogus" },
        new string[] { "Mischmodul", "mischmodul" },
        new string[] { "Connect Four", "connectFourModule" },
        new string[] { "Directing Buttons", "GSDirectingButtons" },
        new string[] { "Macro Memory", "macroMemory" },
        new string[] { "Anomia", "anomia" }
    );
}
