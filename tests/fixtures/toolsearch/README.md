# Tool search requests

`queries.json` is 35 requests someone might type into humcp's tool search, each with the corpus
tools (`server/name`, from `../schemas/`) that would answer it. Written by hand for the v2 LLM spike
(`docs/v2-llm-spike.md` §6) on 2026-10-20 and moved here for Phase 8.

**Unlike the schema corpus and the result fixtures, these are meant to be hand-written**: they are
evaluation data, questions put to the corpus, not things a server published. Several were written so
that they share no words with the right tool ("how high above sea level is Denver" is
`maps_elevation`), because those are the ones only a model can answer; leave them in, since they keep
the lexical score honest about what it cannot do.

Change a request only to fix a wrong answer, and say so in the commit: the score in
`ToolSearchCorpusTests` is measured against this file, and editing the questions to suit the
ranking would measure nothing.
