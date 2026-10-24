using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class NavBarManager : MonoBehaviour
{
    private static NavBarManager _instance;

    [Header("Hide On Scenes")]
    private string[] hideOnScenes = { "GameOverScene", "WinScene", "SplashScene", "MissionZeroScene", "RaceScene" };

    [Header("Nav Buttons")]
    public Button homeButton;
    public Button missionsButton;
    public Button glyphwayButton;
    public Button dictionaryButton;
    public Button shopButton;

    [Header("Highlight Colors")]
    public Color activeColor = Color.white;
    public Color inactiveColor = new Color(1f, 1f, 1f, 0.4f);

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        // Subscribed for the object's whole life (not OnEnable/OnDisable): this object hides
        // itself with SetActive(false), and an OnDisable unsubscribe meant it could never hear
        // the next scene load to show itself again.
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (_instance == this) SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Start()
    {
        HighlightCurrent(SceneManager.GetActiveScene().name);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        bool shouldHide = System.Array.Exists(hideOnScenes, s => s == scene.name);
        gameObject.SetActive(!shouldHide);
        HighlightCurrent(scene.name);
    }

    private void HighlightCurrent(string sceneName)
    {
        SetColor(homeButton, inactiveColor);
        SetColor(missionsButton, inactiveColor);
        SetColor(glyphwayButton, inactiveColor);
        SetColor(dictionaryButton, inactiveColor);
        SetColor(shopButton, inactiveColor);

        switch (sceneName)
        {
            // Home and its sub-pages
            case "TitleScene":
            case "GarageScene":
            case "SettingsScene":
                SetColor(homeButton, activeColor);
                break;

            case "MissionsScene":
            case "MissionZeroScene":
                SetColor(missionsButton, activeColor);
                break;

            case "GlyphwayScene":
                SetColor(glyphwayButton, activeColor);
                break;

            case "DictionaryScene":
                SetColor(dictionaryButton, activeColor);
                break;

            case "ShopScene":
                SetColor(shopButton, activeColor);
                break;
        }
    }

    private void SetColor(Button btn, Color color)
    {
        if (btn == null) return;
        var img = btn.GetComponent<Image>();
        if (img != null) img.color = color;
    }

    /* ---------- Navigation ---------- */
    public void GoToHome()       { SceneManager.LoadScene("TitleScene"); }
    public void GoToMissions()   { SceneManager.LoadScene("MissionsScene"); }
    public void GoToGlyphway()   { SceneManager.LoadScene("GlyphwayScene"); }
    public void GoToDictionary() { SceneManager.LoadScene("DictionaryScene"); }
    public void GoToShop()       { SceneManager.LoadScene("ShopScene"); }
    public void GoToGarage() { SceneManager.LoadScene("GarageScene"); }
}