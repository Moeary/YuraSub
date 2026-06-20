from yurasub.payload import clean_text, pick_text, read_bool


def test_clean_text_normalizes_whitespace_and_lines() -> None:
    assert clean_text("  hello   world \r\n\n translated   line ") == "hello world\ntranslated line"


def test_pick_text_uses_aliases() -> None:
    payload = {"caption": "字幕正文", "translated": "translated text"}

    assert pick_text(payload) == ("字幕正文", "translated text")


def test_read_bool_accepts_common_forms() -> None:
    assert read_bool("yes") is True
    assert read_bool("0") is False
    assert read_bool(None, default=True) is True
