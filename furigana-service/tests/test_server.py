"""Tests for furigana-service/server.py -- pure-function and API tests."""

import sys
import os
import pytest

# Ensure the parent directory is on sys.path so we can import server
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from server import parse_flfl_output, FuriganaSegment


# ---------------------------------------------------------------------------
# Pure-function tests (no heavy deps required)
# ---------------------------------------------------------------------------


class TestParseFlflOutput:
    """Tests for parse_flfl_output."""

    def test_parse_nominal(self):
        """Basic ruby + trailing literal."""
        result = parse_flfl_output(
            '<ruby>国境<rt>くにざかい</rt></ruby>の',
            '国境の',
        )
        assert len(result) == 2
        assert result[0].surface == '国境'
        assert result[0].reading == 'くにざかい'
        assert result[0].is_oov is False
        assert result[1].surface == 'の'
        assert result[1].reading is None

    def test_parse_strips_endoftext(self):
        """endoftext token is stripped, not emitted as segment."""
        raw = '<ruby>国境<rt>くにざかい</rt></ruby>の<|endoftext|>'
        result = parse_flfl_output(raw, '国境の')
        assert len(result) == 2
        assert result[0].surface == '国境'
        assert result[0].reading == 'くにざかい'
        assert result[1].surface == 'の'

    def test_parse_strips_im_tokens(self):
        """im_start and im_end tokens are stripped."""
        raw = '<|im_start|><ruby>長<rt>なが</rt></ruby>い<|im_end|>'
        result = parse_flfl_output(raw, '長い')
        assert len(result) == 2
        assert result[0].surface == '長'
        assert result[0].reading == 'なが'
        assert result[1].surface == 'い'

    def test_parse_mixed_surface(self):
        """Trailing kana that duplicates the reading is merged into the surface."""
        result = parse_flfl_output(
            '<ruby>鰤<rt>ぶり</rt></ruby>ぶり',
            '鰤ぶり',
        )
        assert len(result) == 1
        assert result[0].surface == '鰤ぶり'
        assert result[0].reading == 'ぶり'

    def test_parse_alignment_failure(self):
        """Mismatched original text triggers OOV fallback."""
        result = parse_flfl_output(
            '<ruby>foo<rt>bar</rt></ruby>baz', 'xyz'
        )
        assert len(result) == 1
        assert result[0].surface == 'xyz'
        assert result[0].reading is None
        assert result[0].is_oov is True

    def test_parse_no_ruby(self):
        """Pure literal text (no ruby tags) returns a single segment."""
        result = parse_flfl_output('こんにちは', 'こんにちは')
        assert len(result) == 1
        assert result[0].surface == 'こんにちは'
        assert result[0].reading is None

    def test_parse_multiple_ruby(self):
        """Multiple ruby tags in one sentence."""
        raw = (
            '<ruby>国境<rt>くにざかい</rt></ruby>の'
            '<ruby>長<rt>なが</rt></ruby>い'
        )
        original = '国境の長い'
        result = parse_flfl_output(raw, original)
        assert len(result) == 4
        assert result[0].surface == '国境'
        assert result[0].reading == 'くにざかい'
        assert result[1].surface == 'の'
        assert result[1].reading is None
        assert result[2].surface == '長'
        assert result[2].reading == 'なが'
        assert result[3].surface == 'い'
        assert result[3].reading is None


class TestKata2Hira:
    """Verify jaconv katakana -> hiragana conversion."""

    def test_kata2hira(self):
        import jaconv
        assert jaconv.kata2hira('クニザカイ') == 'くにざかい'

    def test_kata2hira_hiragana_passthrough(self):
        import jaconv
        assert jaconv.kata2hira('くにざかい') == 'くにざかい'

    def test_kata2hira_mixed(self):
        import jaconv
        assert jaconv.kata2hira('アァ') == 'あぁ'


# ---------------------------------------------------------------------------
# API tests (require fastapi + httpx; skipped if unavailable)
# ---------------------------------------------------------------------------

try:
    from fastapi.testclient import TestClient
    from server import app
    HAS_FASTAPI = True
except ImportError:
    HAS_FASTAPI = False


@pytest.mark.skipif(not HAS_FASTAPI, reason='fastapi or httpx not installed')
class TestAPI:
    """Integration tests for the FastAPI routes."""

    def test_health_returns_ok(self):
        client = TestClient(app)
        response = client.get('/health')
        assert response.status_code == 200
        data = response.json()
        assert data['ok'] is True

    def test_status_shape(self):
        client = TestClient(app)
        response = client.get('/status')
        assert response.status_code == 200
        data = response.json()
        assert 'sudachi_ready' in data
        assert 'flfl_loaded' in data
        assert 'flfl_loading' in data
        assert 'flfl_latency_ms' in data
