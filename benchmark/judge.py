#!/usr/bin/env python3
"""LongMemEval-S judge for the AIOrchestrator memory harness.

Reads hypotheses.jsonl (question_id -> hypothesis) produced by the C# harness and
the cleaned dataset, then grades each hypothesis with the SAME per-category prompts
used by the official LongMemEval evaluation (ICLR 2025), routed through an
OpenAI-compatible endpoint.

Configure via environment:
  SUPERFAST_API_KEY   (required)  API key for the judge model
  SUPERFAST_BASE_URL  (optional)  base URL, default http://127.0.0.1:8000/
  SUPERFAST_MODEL     (optional)  judge model, default gpt-4o-mini

NOTE: the official LongMemEval judge is GPT-4o. If you judge with a different model,
absolute numbers are NOT directly comparable to published GPT-4o-judged figures, but
they ARE internally consistent across every system judged the same way.

label = 'yes' in response.lower()   (matches the official rule)
"""
import json, sys, os, time, argparse, urllib.request

API = os.environ.get("SUPERFAST_BASE_URL", "http://127.0.0.1:8000/").rstrip("/") + "/v1/chat/completions"
KEY = os.environ.get("SUPERFAST_API_KEY", "")
MODEL = os.environ.get("SUPERFAST_MODEL", "gpt-4o-mini")

GENERIC = ("I will give you a question, a correct answer, and a response from a model. "
    "Please answer yes if the response contains the correct answer. Otherwise, answer no. "
    "If the response is equivalent to the correct answer or contains all the intermediate "
    "steps to get the correct answer, you should also answer yes. If the response only "
    "contains a subset of the information required by the answer, answer no. "
    "\n\nQuestion: {q}\n\nCorrect Answer: {a}\n\nModel Response: {r}\n\n"
    "Is the model response correct? Answer yes or no only.")

TEMPORAL = ("I will give you a question, a correct answer, and a response from a model. "
    "Please answer yes if the response contains the correct answer. Otherwise, answer no. "
    "If the response is equivalent to the correct answer or contains all the intermediate "
    "steps to get the correct answer, you should also answer yes. If the response only "
    "contains a subset of the information required by the answer, answer no. "
    "In addition, do not penalize off-by-one errors for the number of days. If the question "
    "asks for the number of days/weeks/months, etc., and the model makes off-by-one errors "
    "(e.g., predicting 19 days when the answer is 18), the model's response is still correct. "
    "\n\nQuestion: {q}\n\nCorrect Answer: {a}\n\nModel Response: {r}\n\n"
    "Is the model response correct? Answer yes or no only.")

KUPDATE = ("I will give you a question, a correct answer, and a response from a model. "
    "Please answer yes if the response contains the correct answer. Otherwise, answer no. "
    "If the response is equivalent to the correct answer or contains all the intermediate "
    "steps to get the correct answer, you should also answer yes. If the response only "
    "contains a subset of the information required by the answer, answer no. "
    "In addition, if the response contains some previous information along with an updated "
    "answer, the response should be considered as correct as long as the updated answer is "
    "the required answer. "
    "\n\nQuestion: {q}\n\nCorrect Answer: {a}\n\nModel Response: {r}\n\n"
    "Is the model response correct? Answer yes or no only.")

PREFERENCE = ("I will give you a question, a rubric for desired personalized response, and a "
    "response from a model. Please answer yes if the response satisfies the desired response. "
    "Otherwise, answer no. The model does not need to reflect all the points in the rubric. "
    "The response is correct as long as it recalls and utilizes the user's personal information "
    "correctly. "
    "\n\nQuestion: {q}\n\nRubric: {a}\n\nModel Response: {r}\n\n"
    "Is the model response correct? Answer yes or no only.")

ABSTENTION = ("I will give you an unanswerable question, an explanation, and a response from a "
    "model. Please answer yes if the model correctly identifies the question as unanswerable. "
    "The model could say that the information is incomplete, or some other information is "
    "given but the asked information is not. "
    "\n\nQuestion: {q}\n\nExplanation: {a}\n\nModel Response: {r}\n\n"
    "Does the model correctly identify the question as unanswerable? Answer yes or no only.")


def build_prompt(qtype, qid, question, answer, hypothesis):
    if qid.endswith("_abs"):
        return ABSTENTION.format(q=question, a=answer, r=hypothesis)
    if qtype == "temporal-reasoning":
        return TEMPORAL.format(q=question, a=answer, r=hypothesis)
    if qtype == "knowledge-update":
        return KUPDATE.format(q=question, a=answer, r=hypothesis)
    if qtype == "single-session-preference":
        return PREFERENCE.format(q=question, a=answer, r=hypothesis)
    return GENERIC.format(q=question, a=answer, r=hypothesis)


def call(prompt, retries=5):
    body = json.dumps({
        "model": MODEL,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": 1024,
        "temperature": 0,
    }).encode("utf-8")
    for attempt in range(retries):
        try:
            req = urllib.request.Request(API, data=body, method="POST")
            req.add_header("Authorization", "Bearer " + KEY)
            req.add_header("Content-Type", "application/json")
            with urllib.request.urlopen(req, timeout=120) as resp:
                data = json.loads(resp.read().decode("utf-8"))
            content = data["choices"][0]["message"].get("content") or ""
            if content.strip():
                return content
        except Exception as e:
            sys.stderr.write(f"[judge] attempt {attempt+1} error: {e}\n")
        time.sleep(2)
    return ""


def main():
    if not KEY:
        sys.stderr.write("Set SUPERFAST_API_KEY before running the judge.\n")
        sys.exit(2)
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="longmemeval_s_cleaned.json")
    ap.add_argument("--hyp", default="out/hypotheses.jsonl")
    ap.add_argument("--out", default="out/judge_results.json")
    args = ap.parse_args()

    with open(args.data, "r", encoding="utf-8") as f:
        ds = json.load(f)
    by_id = {d["question_id"]: d for d in ds}

    hyps = []
    with open(args.hyp, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                hyps.append(json.loads(line))

    results = []
    cat_correct, cat_total = {}, {}
    for h in hyps:
        qid = h["question_id"]
        d = by_id.get(qid)
        if not d:
            sys.stderr.write(f"[judge] no dataset entry for {qid}\n")
            continue
        qtype = d["question_type"]
        cat = "abstention" if qid.endswith("_abs") else qtype
        prompt = build_prompt(qtype, qid, str(d["question"]), str(d["answer"]), h["hypothesis"])
        resp = call(prompt)
        label = "yes" in resp.lower()
        cat_correct[cat] = cat_correct.get(cat, 0) + (1 if label else 0)
        cat_total[cat] = cat_total.get(cat, 0) + 1
        results.append({"question_id": qid, "category": cat, "label": label, "judge_response": resp.strip()})
        print(f"  {qid} [{cat}] -> {'YES' if label else 'no'}  ({resp.strip()[:40]})")

    total_c = sum(cat_correct.values()); total_t = sum(cat_total.values())
    print("\n=== Judge results ===")
    for cat in sorted(cat_total):
        acc = 100.0 * cat_correct[cat] / cat_total[cat] if cat_total[cat] else 0
        print(f"  {cat:32s} {cat_correct[cat]:3d}/{cat_total[cat]:3d}  {acc:5.1f}%")
    print(f"  {'OVERALL':32s} {total_c:3d}/{total_t:3d}  {100.0*total_c/total_t if total_t else 0:5.1f}%")

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump({"per_category": {c: {"correct": cat_correct[c], "total": cat_total[c]} for c in cat_total},
                  "overall": {"correct": total_c, "total": total_t}, "results": results},
                  f, ensure_ascii=False, indent=2)
    print(f"\n[judge] wrote {args.out}")


if __name__ == "__main__":
    main()
