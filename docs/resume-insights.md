# Resume gaps (Insights)

[← Back to the README](../README.md)

The **Insights** page answers "what should my resume say so that more of the jobs I collect are worth applying for?". It works on the jobs that already have a match score:

- **Skill gaps.** The "missing skills" of every job are counted. Different wordings of one skill ("K8s", "Kubernetes", "Docker and Kubernetes (not shown)") are merged; soft skills and requirements about you (work permit, location) are listed apart because a resume cannot add them.
- **What to change first.** A greedy plan picks, step by step, the skill that lifts the most jobs over your threshold (60-80%, default from the dashboard minimum). Points per skill come from a regression fitted to your own scores when the data supports it (**Math.NET Numerics**), otherwise from an equal-share assumption, and the page says which. The expected result comes with a range from a Monte Carlo simulation.
- **Keywords and job families.** **ML.NET** builds a TF-IDF text model of the posting texts (only those in your resume's language) and groups them with K-Means; words many postings use and the resume does not are listed.
- **Claude's advice (on request).** Pressing the button sends your resume text and the skills found to the selected AI provider. Every suggestion is checked afterwards: it must be about a skill from the list, "show it" needs a quote that is really in your resume, and wording with a number the resume does not contain is dropped. Skills your resume does not support are listed as "learn first" - the page never suggests claiming what is not true.

Limits, also stated on the page: the text similarity agrees only weakly with Claude's scores, the plan is a greedy approximation, and it describes the jobs you collected, not the market. Nothing leaves your computer except when you press the Claude button.
