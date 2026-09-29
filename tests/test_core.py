import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src"))

from visual_search_core.models import TrickplayMetadata
from visual_search_core.retry import backoff_seconds
from visual_search_core.scoring import aggregate_frame_scores, fuse_scores
from visual_search_core.trickplay import locate_frame, uniform_sample_indices


def test_trickplay_tile_mapping_across_tiles():
    metadata = TrickplayMetadata(10000, 103, 10, 10, 320, 180)
    assert locate_frame(0, metadata).tile_index == 0
    assert (locate_frame(99, metadata).row, locate_frame(99, metadata).column) == (9, 9)
    assert locate_frame(100, metadata).tile_index == 1
    assert (locate_frame(102, metadata).row, locate_frame(102, metadata).column) == (0, 2)
    assert locate_frame(102, metadata).timestamp_ms == 1_020_000


def test_uniform_sampling_is_deterministic_and_bounded():
    assert uniform_sample_indices(10, 4) == [0, 3, 6, 9]
    assert uniform_sample_indices(3, 10) == [0, 1, 2]
    assert uniform_sample_indices(0, 5) == []


def test_hybrid_aggregation_and_best_frame():
    result = aggregate_frame_scores([
        ("v1", 1, 1000, 0.92),
        ("v1", 2, 2000, 0.80),
        ("v1", 3, 3000, 0.60),
        ("v1", 4, 4000, 0.10),
    ])['v1']
    assert result.best_frame.frame_index == 1
    assert round(result.visual_score, 4) == round(0.7 * 0.92 + 0.3 * ((0.92 + 0.80 + 0.60) / 3), 4)


def test_fusion_renormalizes_missing_modalities():
    assert abs(fuse_scores(0.8, 0.6) - 0.75) < 1e-9
    assert abs(fuse_scores(0.8, None) - 0.8) < 1e-9
    assert abs(fuse_scores(None, 0.6) - 0.6) < 1e-9


def test_backoff_caps_at_last_delay():
    assert [backoff_seconds(i) for i in range(5)] == [10, 30, 60, 300, 300]
