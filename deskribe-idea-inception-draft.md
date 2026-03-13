# Deskribe Idea Inception Draft

## Working Title

**Deskribe** — Declarative intent capture for platform delivery.

## Why this exists

Teams should not need to become platform engineers just to ship a service.

Today, developers often need to touch YAML, Terraform, cloud-specific configuration, deployment repositories, and other platform-facing artifacts just to ask for normal things such as a database, cache, messaging topic, alerts, or environment variables. That creates cognitive load, slows delivery, and pushes developers into tools and concerns that are not their area of expertise.

At the same time, platform and cloud teams end up spending valuable time on repetitive enablement work: helping teams get started, reviewing infrastructure definitions, correcting mistakes, explaining standards, and handling one-off changes that could be automated.

The result is that both sides are forced into each other’s world:

- Developers become part-time platform engineers.
- Platform teams become part-time reviewers, teachers, and babysitters for repetitive setup work.

This does not scale well, especially in organizations where many services share similar patterns: microservices, observability, logging, alerts, databases, caches, messaging, and mixed tenancy models.

Deskribe exists to create the missing layer in the middle: **intent capture**.

Developers describe **what** they need.  
Platform teams define **how** it is provisioned, secured, wired, and operated.  
Deskribe becomes the contract and automation layer between both.

---

## Problem Statement

### Developer pain

Developers want to build product logic, not become experts in:

- Terraform
- Kubernetes YAML
- cloud networking
- IAM and secrets wiring
- platform deployment conventions
- separate deployment repositories

A common frustration is needing to jump between the application repository and a separate deployments repository to perform what is logically one action, such as changing an environment variable or adding a dependency.

Even when this setup works, it only works until something unusual happens. The moment a team needs something slightly outside the happy path, the process becomes manual again.

### Platform team pain

The hidden cost for platform teams is not only infrastructure ownership. It is also the repeated support burden around infrastructure definitions created or modified by application teams.

This usually shows up as:

- onboarding and hand-holding for teams starting services
- explaining the same platform concepts repeatedly
- reviewing repetitive YAML and Terraform changes
- correcting infra definitions that do not match standards
- handling one-off requests that should have been standardized
- meetings to provision common things such as Kafka topics, alerts, or service defaults

The issue is not only the number of new projects. The issue is the recurring manual effort every time a team needs something that is not routine for them.

That effort is expensive because it consumes time from the people who should be investing in better platform abstractions and automation.

---

## Core Insight

The current process assumes that application teams should become progressively self-sufficient by learning the same tools the platform team uses.

That sounds good in theory, but in practice it means every developer needs to become partially fluent in infrastructure and deployment concerns that are outside their main discipline.

This creates duplication of knowledge, inconsistent standards, more review overhead, and more places where changes can fail.

A better model is not “teach every team how to do platform work.”

A better model is:

- developers express intent
- platform teams own reusable recipes, standards, and guardrails
- automation connects the two

---

## Vision

A developer should be able to say:

- this service needs a database
- this service needs a cache
- this service subscribes to these topics
- this service needs these environment variables
- this service should be production-ready by default

…and then stop thinking about the low-level plumbing.

Deskribe should take that intent and translate it into the files, formats, and platform interactions required by the organization.

The platform team should remain in full control of:

- approved provisioning patterns
- Terraform modules
- deployment standards
- networking rules
- tenancy models
- observability defaults
- security constraints
- runtime-specific requirements

In other words:

**Developers own intent. Platform teams own implementation. Deskribe owns the glue.**

---

## Positioning

Deskribe should be positioned as:

- an intent definition layer
- a contract between developers and platform teams
- a policy and defaults engine
- a translator into platform-owned infrastructure workflows

Deskribe should **not** be positioned as:

- a replacement for platform engineering
- a replacement for Terraform
- a replacement for deployment tooling
- a system that takes control away from cloud or infra teams

It should help platform teams scale their standards, not bypass them.

---

## Relationship with Aspire

Deskribe should treat **Aspire as a framework to leverage**, not as a competitor.

### Aspire’s role

Aspire is strong for:

- local distributed application orchestration
- service graph modeling
- dependency wiring
- local development experience
- observability and dashboarding during development

### Deskribe’s role alongside Aspire

Deskribe can use Aspire to improve the inner loop by mapping declared service intent into a local application topology.

That means:

- Deskribe describes the service and its needs
- Aspire helps run and wire those dependencies for local development
- platform-owned provisioning still happens through existing infrastructure workflows

This makes Deskribe complementary to Aspire:

- **Aspire** helps developers run applications and dependencies locally
- **Deskribe** helps developers declare what they need and helps platform teams fulfill it in a standardized way

### Practical positioning

A useful framing is:

- Aspire = local app model and developer runtime
- Terraform = platform-owned provisioning engine
- Deskribe = glue, policy, and contract between developer intent and platform delivery

---

## Proposed Architecture

## 1. Two planes

### Developer plane

Purpose:
- simplify local setup
- remove manual platform ceremony from the inner loop
- provide a clean way to express dependencies

How:
- developer writes a `deskribe.json` (or equivalent)
- Deskribe resolves dependencies and service intent
- Aspire is used to run the service graph locally where appropriate

Outcome:
- better local development ergonomics
- fewer manual steps
- less need to understand low-level platform concerns in the inner loop

### Platform plane

Purpose:
- preserve cloud/platform ownership
- reuse existing Terraform and deployment standards
- centralize policy, defaults, and environment-specific behavior

How:
- platform team owns a capability catalog and implementation mappings
- Deskribe resolves intent into approved platform workflows
- Terraform modules and existing deployment tooling remain the source of truth for provisioning
- Deskribe generates the integration artifacts needed to bind applications to provisioned resources

Outcome:
- platform standards are preserved
- repetitive support work is reduced
- developers do not need to author infrastructure definitions directly

---

## 2. Capability model

Deskribe should expose a small set of developer-facing capabilities instead of low-level infrastructure primitives.

Examples:

- postgres
- redis
- kafka
- blob storage
- object storage
- alerts
- observability
- secrets/config
- multi-tenant or single-tenant service shape

Developers request capabilities.

Platform teams define:

- which capability options are allowed
- which implementation backs each capability
- which defaults and policies apply
- what outputs or bindings are guaranteed

This lets Deskribe stay stable even if platform internals evolve.

---

## 3. Bindings as the contract

One of the key outputs of the system should be a stable machine-readable bindings artifact.

This artifact should describe how an application consumes provisioned resources without forcing developers to understand how those resources were created.

Examples of what bindings may carry:

- connection string secret references
- topic names
- bootstrap server references
- cache endpoints
- config references
- environment-specific values
- tenancy-specific routing details

This becomes the bridge between provisioning and application wiring.

---

## 4. Minimal generated deployment glue

Deskribe should not try to own every deployment artifact.

Instead, it should focus on generating only what is needed to bridge app intent with platform-owned delivery mechanisms.

That may include:

- thin Terraform input layers
- generated values files
- secret/config bindings
- runtime wiring artifacts
- environment-specific resolved outputs

The goal is to reduce manual authoring, not to create a second platform stack.

---

## Why this matters strategically

This is not only a developer experience improvement.

It is a platform scaling strategy.

Without intent capture:

- platform expertise gets diluted into repetitive enablement work
- standards live in tribal knowledge and PR review comments
- every team re-learns the same lessons
- infrastructure changes remain high-friction
- “do it once and forget it” becomes false in practice

With intent capture:

- common service patterns become reusable
- standards become codified
- platform teams can invest in better abstractions instead of babysitting
- developers stay focused on product work
- changes become more consistent and less error-prone

---

## Example message this system enables

Instead of:

- editing YAML in one repository
- editing deployment values in another repository
- coordinating a platform review
- scheduling a meeting for a Kafka topic or alert setup

a team should be able to express something closer to:

> This service needs PostgreSQL, Redis, two Kafka subscriptions, production-ready observability, and these configuration values.

Then Deskribe handles the rest by applying organization-approved standards.

---

## Initial Product Principles

1. **Intent first**  
   Developers should describe what they need, not how to provision it.

2. **Platform-owned implementation**  
   Cloud and infra teams must remain in control of the underlying machinery.

3. **Reuse before reinvention**  
   Reuse Aspire for local developer workflows and reuse existing Terraform/platform investments for provisioning.

4. **Codify standards**  
   Repeated guidance should become platform rules and templates, not tribal knowledge.

5. **Keep the abstraction honest**  
   Deskribe should simplify the common path without pretending infrastructure complexity does not exist.

6. **Reduce cognitive load**  
   One logical change should not require jumping across multiple repositories and toolchains.

---

## MVP Direction

A first proof of concept could focus on a narrow but high-value path:

- one service manifest format
- a few common capabilities such as postgres, redis, kafka, and env vars
- local development integration using Aspire
- platform mapping into existing Terraform workflows
- generation of a bindings artifact
- one deployment target or one environment path

The goal of the MVP is not to solve every platform scenario.

The goal is to prove that:

- developers can declare intent in one place
- platform teams can keep ownership
- repeated support work can be reduced
- standards can be applied automatically

---

## Short Pitch

Deskribe is an intent-capture layer that lets developers describe what their service needs while allowing platform teams to keep full ownership of how those needs are provisioned, secured, and operated.

It uses automation to remove repetitive YAML/Terraform glue work, reduce cross-team friction, and turn platform standards into reusable delivery paths.

---

## One-line summary

**Deskribe lets developers describe what they need and lets platform teams deliver it consistently, without forcing both sides to do each other’s jobs.**

---

## Source Context

This draft was shaped from prior discussion about the pain of YAML/Terraform-heavy workflows, the hidden support burden on platform teams, the desire to use Aspire as a complement rather than a competitor, and the goal of making Deskribe the glue between developer intent and platform-owned delivery. fileciteturn0file0L1-L20
