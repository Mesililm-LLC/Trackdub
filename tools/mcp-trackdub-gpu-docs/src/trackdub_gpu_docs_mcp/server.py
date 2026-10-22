from __future__ import annotations

from typing import Any

from fastmcp import FastMCP

from . import __version__
from .corpus import load_sources, pin_summary
from .search import get_source_text, search_chunks


mcp = FastMCP(
    name="trackdub-gpu-docs",
    instructions=(
        "Curated Trackdub GPU / TensorRT-RTX / ORT plugin EP / Windows ML docs. "
        "Keyed to TRT-RTX EP ABI 0.5.0 (win, TRT-RTX 1.7.1) / 0.4.0 (linux via LinML, TRT-RTX 1.6.1) cu13. Prefer Trackdub local sources for pin policy. "
        "Use NVIDIA CUDA MCP for CUDA programming questions; use this server for TRT-RTX EP wiring."
    ),
)


@mcp.tool()
def list_corpus() -> dict[str, Any]:
    """List allowlisted corpus sources and the live TRT-RTX EP pin."""
    sources = [
        {
            "id": s.id,
            "kind": s.kind,
            "title": s.title,
            "tags": list(s.tags),
            "priority": s.priority,
            "path": s.path,
            "url": s.url,
            "pinWarning": s.pin_warning,
        }
        for s in load_sources()
    ]
    return {
        "serverVersion": __version__,
        "pin": pin_summary(),
        "sourceCount": len(sources),
        "sources": sources,
    }


@mcp.tool()
def search_trackdub_gpu_docs(query: str, limit: int = 8, tag: str | None = None) -> dict[str, Any]:
    """Keyword search over Trackdub GPU docs and ingested NVIDIA/ORT/WinML pages.

    Prefer queries like: 'RegisterExecutionProviderLibrary NvTensorRTRTX',
    'runtime cache EngineCache', 'standalone plugin vs Windows ML catalog'.
    Optional tag filter examples: trackdub-local, ort-plugin-ep, nvidia-tensorrt-rtx, microsoft-winml.
    """
    hits = search_chunks(query, limit=limit, tag=tag)
    return {
        "query": query,
        "tag": tag,
        "pin": pin_summary(),
        "hitCount": len(hits),
        "hits": hits,
        "hint": (
            "If remote hits are missing, run ingest from tools/mcp-trackdub-gpu-docs: "
            "uv run trackdub-gpu-docs-ingest"
        ),
    }


@mcp.tool()
def get_doc(source_id: str, max_chars: int = 12000) -> dict[str, Any]:
    """Fetch full text for one corpus source id from list_corpus / search hits."""
    return get_source_text(source_id, max_chars=max_chars)


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
