"""Embed one stdin text with the live Chroma model (BAAI/bge-small-en-v1.5, 384-d).

Does not open pgvector, does not read a vault, and does not change the memory backend.
"""
import sys

from chromadb.utils.embedding_functions import SentenceTransformerEmbeddingFunction

MODEL = "BAAI/bge-small-en-v1.5"
DIMENSIONS = 384


def main() -> int:
    text = sys.stdin.read()
    embed = SentenceTransformerEmbeddingFunction(model_name=MODEL)
    vector = list(embed([text])[0])
    if len(vector) != DIMENSIONS:
        print(f"expected {DIMENSIONS} dimensions, got {len(vector)}", file=sys.stderr)
        return 2
    sys.stdout.write(" ".join(format(float(value), ".8g") for value in vector))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
