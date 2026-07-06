# J2E OCR Translator

J2E OCR Translator is a Windows desktop application built with WPF (.NET 8) for translating Japanese text from images to English. It features OCR (Optical Character Recognition) using both Manga OCR and a custom OCR model, and integrates translation via Google Translate and DeepL APIs. The app also provides a built-in Japanese-English dictionary search.

## Features

- **Screen Region Selection:** Select any region of your screen to capture and extract Japanese text.
- **OCR Models:** Toggle between Manga OCR and a custom OCR model for text recognition.
- **Translation:** Instantly translate recognized Japanese text to English using Google Translate or DeepL.
- **Dictionary Lookup:** Search for Japanese words and view readings and meanings.
- **Furigana (Ruby Readings):** Display hiragana phonetic readings above kanji in the overlay. Uses a Python sidecar with SudachiPy for dictionary words and FLFL (1B LLM) for out-of-vocabulary tokens. Falls back to Ollama when FLFL is too slow.
- **Editable Results:** Edit OCR results and re-translate as needed.
- **Modern UI:** Uses WPF-UI for a clean, dark-themed interface.

## Screenshots

### Display Translation

![Display Translation](asset/translate.png)

### Edit OCR Text

![Edit OCR Text](asset/edit.png)

### Dictionary Search

![Dictionary Search](asset/search.png)

## Getting Started

### Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| Windows | 10/11 | Required for WPF |
| .NET SDK | 8.0+ | For building the app |
| Python | 3.10+ (3.12 recommended) | For OCR and furigana sidecar |

### 1. Clone & Restore

```bash
git clone <repo-url>
cd Onscreen-Translator
dotnet restore
```

### 2. Configure API Keys

Create a `.env` file in the project root:

```
DEEPL_API_KEY=your_deepl_key
GOOGLE_API_KEYS=your_google_key
```

### 3. Set Up Furigana Sidecar (Optional but Recommended)

The furigana feature uses a Python sidecar service for morphological analysis. It runs locally on `127.0.0.1:8765`.

**Disk space:** ~650 MB for models (Sudachi core dict ~50 MB, FLFL weights ~600 MB).

```bash
cd furigana-service

# Windows
install.bat
run.bat

# Linux / macOS
chmod +x install.sh run.sh
./install.sh
./run.sh
```

The app can also start the sidecar automatically from the Settings tab when furigana is enabled.

**Without the sidecar:** The app works fine — furigana just won't be available. All other features (OCR, translation, dictionary search) work without Python.

#### Sharing Models Across Machines

To reuse downloaded models (Sudachi dict + FLFL weights) from a shared location (NAS, network drive, etc.), set these environment variables before running `install.sh`/`run.sh`:

| Variable | Default | Description |
|---|---|---|
| `MODELS_DIR` | `<script>/models` | Base directory for all model files |
| `HF_HOME` | `$MODELS_DIR/huggingface` | Hugging Face cache directory |
| `SUDACHIDICT_DIR` | `$MODELS_DIR/sudachi` | Sudachi dictionary directory |
| `FLFL_MODEL` | `Calvin-Xu/FLFL` | FLFL model: HuggingFace ID or local path |

Example — point to a NAS share:

```bash
export MODELS_DIR=/mnt/nas/models
export FLFL_MODEL=/mnt/nas/models/huggingface/hub/models--Calvin-Xu--FLFL
./run.sh
```

On Windows:

```bat
set MODELS_DIR=\\nas\share\models
set FLFL_MODEL=\\nas\share\models\huggingface\hub\models--Calvin-Xu--FLFL
run.bat
```

Install once on the machine with the most storage, then point all other machines to the same `MODELS_DIR`. The only local requirement is the Python venv with dependencies — models are read from the shared path.

### 4. Set Up OCR (Optional)

If you need the custom OCR model, set up the Python environment as referenced in `custom_ocr/ocr.py`.

### 5. Build & Run

```bash
dotnet build
dotnet run --project WpfAppTest.csproj
```

## Usage

- **Select Region:** Click and drag to select a region of the screen.
- **OCR & Translate:** The app will extract Japanese text and translate it to English.
- **Edit & Re-translate:** Double-click the translation to edit the original text and update the translation.
- **Dictionary Search:** Use the search panel to look up Japanese words.
- **Furigana:** Right-click the overlay or press **F** to cycle through views:
  - **Translation** (default) — English translation
  - **Furigana** — hiragana readings above kanji
  - **Original** — raw OCR text

## Project Structure

- `MainWindow.xaml` / `.cs`: Main UI and logic.
- `MangaOCR.cs`: Python OCR integration via pythonnet.
- `Translate.cs`: Translation API integration.
- `DictionaryLookup.cs`: Japanese-English dictionary search.
- `Controls/RubyTextBlock.cs`: Custom WPF control for hiragana ruby rendering.
- `OverlayState.cs`: Per-region overlay state (translation, furigana, view mode).
- `FuriganaServiceManager.cs`: Sidecar lifecycle, health checks, auto-degradation.
- `Providers/Furigana/`: Furigana provider interfaces and HTTP client.
- `furigana-service/`: Python FastAPI sidecar (SudachiPy + FLFL).
- `custom_ocr/`: Python custom OCR implementation.

## License

This project is for educational and personal use. Please respect third-party API terms and model licenses.

## Credits

- [Manga OCR](https://github.com/kha-white/manga-ocr)
- [DeepL](https://www.deepl.com/)
- [Google Translate](https://cloud.google.com/translate)
- [WPF-UI](https://github.com/lepoco/wpfui)
- [SudachiPy](https://github.com/WorksApplications/sudachi.rs) — Japanese morphological analyzer
- [FLFL](https://github.com/Calvin-Xu/FLFL) — furigana generation LLM