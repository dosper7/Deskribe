# CI/CD Integration

This guide explains how to integrate Deskribe into your CI/CD pipelines. Deskribe is designed so that the exact same commands you run locally work identically in CI -- no special CI mode, no extra flags, no drift between what you test and what you ship.

---

## 1. CI/CD Philosophy

### The Same Commands Everywhere

Deskribe does not have a "CI mode". The CLI commands you use on your laptop are the same ones your pipeline runs:

```
Local developer machine          CI/CD pipeline
--------------------------       --------------------------
deskribe validate ...            deskribe validate ...
deskribe plan ...                deskribe plan ...
deskribe apply ...               deskribe apply ...
```

This means:
- **No surprises** -- if validate passes locally, it passes in CI.
- **No CI-specific config** -- your `deskribe.json` and platform config are the only inputs.
- **Easy debugging** -- reproduce any CI failure by running the same command locally.

### The Pipeline Flow

Every Deskribe CI pipeline follows the same four-stage flow:

```
  +----------+     +----------+     +------+     +---------+     +-------+
  |  BUILD   | --> | VALIDATE | --> | PLAN | --> | APPROVE | --> | APPLY |
  |          |     |          |     |      |     |         |     |       |
  | dotnet   |     | deskribe |     | desk |     | Manual  |     | desk  |
  | build    |     | validate |     | plan |     | gate or |     | apply |
  | dotnet   |     |          |     |      |     | auto    |     |       |
  | test     |     | Exit 1   |     | Post |     |         |     | Prov. |
  | docker   |     | = fail   |     | as   |     |         |     | infra |
  | push     |     | pipeline |     | PR   |     |         |     | + K8s |
  |          |     |          |     | cmnt |     |         |     |       |
  +----------+     +----------+     +------+     +---------+     +-------+
       |                |               |              |              |
       v                v               v              v              v
   Container       Policy check     Human-readable   Gate before   Infrastructure
   image ready     before plan      diff review      production    provisioned +
                                                                   app deployed
```

**Key principles:**

1. **Build first** -- compile, test, and push the container image before touching infrastructure.
2. **Validate early** -- catch policy violations before generating a plan. This is cheap and fast.
3. **Plan for visibility** -- always generate a plan so reviewers can see exactly what will change.
4. **Gate before apply** -- never apply to production without an approval step (manual or automated).
5. **Apply atomically** -- apply provisions infrastructure and deploys the workload in one step.

---

## 2. GitHub Actions -- Full Working Example

Below is a complete, production-ready workflow that handles the entire lifecycle:

```yaml
# .github/workflows/deploy.yml
#
# Triggers:
#   - push to main      -> build + validate + plan (posts plan as PR comment)
#   - workflow_dispatch  -> apply to a specific environment (manual trigger)
#   - tag push (v*)      -> apply to production

name: Deskribe Deploy

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]
  workflow_dispatch:
    inputs:
      environment:
        description: "Target environment (dev, staging, prod)"
        required: true
        default: "dev"
        type: choice
        options:
          - dev
          - staging
          - prod
      action:
        description: "Action to perform"
        required: true
        default: "plan"
        type: choice
        options:
          - plan
          - apply
  push:
    tags:
      - "v*"

# Cancel in-progress runs for the same branch
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

env:
  DOTNET_VERSION: "10.0.x"
  REGISTRY: ghcr.io
  IMAGE_NAME: ${{ github.repository }}
  PLATFORM_CONFIG_REPO: acme/platform-config
  PLATFORM_CONFIG_PATH: ./platform-config

jobs:
  # ================================================================
  # Job 1: Build, test, and push the container image
  # ================================================================
  build:
    runs-on: ubuntu-latest
    outputs:
      image-tag: ${{ steps.meta.outputs.tags }}
      image-digest: ${{ steps.build-push.outputs.digest }}
    permissions:
      contents: read
      packages: write

    steps:
      - name: Checkout application repo
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Restore dependencies
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Test
        run: dotnet test --no-build --configuration Release --verbosity normal

      - name: Docker meta
        id: meta
        uses: docker/metadata-action@v5
        with:
          images: ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}
          tags: |
            type=sha,prefix=sha-
            type=ref,event=branch
            type=semver,pattern={{version}}

      - name: Login to GitHub Container Registry
        uses: docker/login-action@v3
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push Docker image
        id: build-push
        uses: docker/build-push-action@v6
        with:
          context: .
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}

  # ================================================================
  # Job 2: Validate the manifest against platform policies
  # ================================================================
  validate:
    needs: build
    runs-on: ubuntu-latest
    strategy:
      matrix:
        environment: [dev, staging, prod]

    steps:
      - name: Checkout application repo
        uses: actions/checkout@v4

      - name: Checkout platform config
        uses: actions/checkout@v4
        with:
          repository: ${{ env.PLATFORM_CONFIG_REPO }}
          path: ${{ env.PLATFORM_CONFIG_PATH }}
          token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Install Deskribe CLI
        run: dotnet tool install --global deskribe-cli

      - name: Validate manifest (${{ matrix.environment }})
        run: |
          deskribe validate \
            -f deskribe.json \
            --env ${{ matrix.environment }} \
            --platform ${{ env.PLATFORM_CONFIG_PATH }}

  # ================================================================
  # Job 3: Generate a plan and post it as a PR comment
  # ================================================================
  plan:
    needs: [build, validate]
    runs-on: ubuntu-latest
    env:
      TARGET_ENV: ${{ github.event.inputs.environment || 'dev' }}

    steps:
      - name: Checkout application repo
        uses: actions/checkout@v4

      - name: Checkout platform config
        uses: actions/checkout@v4
        with:
          repository: ${{ env.PLATFORM_CONFIG_REPO }}
          path: ${{ env.PLATFORM_CONFIG_PATH }}
          token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Install Deskribe CLI
        run: dotnet tool install --global deskribe-cli

      - name: Generate plan
        id: plan
        run: |
          PLAN_OUTPUT=$(deskribe plan \
            -f deskribe.json \
            --env ${{ env.TARGET_ENV }} \
            --platform ${{ env.PLATFORM_CONFIG_PATH }} \
            --image api=${{ needs.build.outputs.image-tag }} 2>&1)

          echo "plan<<EOF" >> $GITHUB_OUTPUT
          echo "$PLAN_OUTPUT" >> $GITHUB_OUTPUT
          echo "EOF" >> $GITHUB_OUTPUT

      - name: Post plan as PR comment
        if: github.event_name == 'pull_request'
        uses: actions/github-script@v7
        with:
          script: |
            const plan = `${{ steps.plan.outputs.plan }}`;
            const body = `## Deskribe Plan (\`${{ env.TARGET_ENV }}\`)

            \`\`\`
            ${plan}
            \`\`\`

            *Generated by Deskribe in CI. Run \`deskribe plan --env ${{ env.TARGET_ENV }}\` locally to reproduce.*`;

            github.rest.issues.createComment({
              issue_number: context.issue.number,
              owner: context.repo.owner,
              repo: context.repo.repo,
              body: body
            });

      - name: Upload plan artifact
        uses: actions/upload-artifact@v4
        with:
          name: deskribe-plan-${{ env.TARGET_ENV }}
          path: .deskribe/plan.json
          retention-days: 30

  # ================================================================
  # Job 4: Apply (requires approval for staging/prod)
  # ================================================================
  apply:
    needs: [build, validate, plan]
    if: |
      (github.event_name == 'workflow_dispatch' && github.event.inputs.action == 'apply') ||
      (github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v'))
    runs-on: ubuntu-latest
    environment: ${{ github.event.inputs.environment || 'prod' }}
    env:
      TARGET_ENV: ${{ github.event.inputs.environment || 'prod' }}

    steps:
      - name: Checkout application repo
        uses: actions/checkout@v4

      - name: Checkout platform config
        uses: actions/checkout@v4
        with:
          repository: ${{ env.PLATFORM_CONFIG_REPO }}
          path: ${{ env.PLATFORM_CONFIG_PATH }}
          token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Install Deskribe CLI
        run: dotnet tool install --global deskribe-cli

      - name: Configure cloud credentials
        uses: azure/login@v2
        with:
          creds: ${{ secrets.AZURE_CREDENTIALS }}

      - name: Configure Kubernetes context
        uses: azure/aks-set-context@v4
        with:
          resource-group: ${{ secrets.AKS_RESOURCE_GROUP }}
          cluster-name: ${{ secrets.AKS_CLUSTER_NAME }}

      - name: Apply
        run: |
          deskribe apply \
            -f deskribe.json \
            --env ${{ env.TARGET_ENV }} \
            --platform ${{ env.PLATFORM_CONFIG_PATH }} \
            --image api=${{ needs.build.outputs.image-tag }}

      - name: Post apply result
        if: always()
        uses: actions/github-script@v7
        with:
          script: |
            const status = '${{ job.status }}' === 'success' ? 'succeeded' : 'failed';
            const emoji = status === 'succeeded' ? 'white_check_mark' : 'x';
            github.rest.repos.createCommitStatus({
              owner: context.repo.owner,
              repo: context.repo.repo,
              sha: context.sha,
              state: status === 'succeeded' ? 'success' : 'failure',
              description: `Deskribe apply ${status} for ${{ env.TARGET_ENV }}`,
              context: 'deskribe/apply/${{ env.TARGET_ENV }}'
            });
```

### Passing `--image` from the Build Step

The `--image` flag maps a service name to the container image built in CI. This ensures the exact image that was built and tested is the one that gets deployed:

```yaml
# The build job outputs the image tag
outputs:
  image-tag: ${{ steps.meta.outputs.tags }}

# Later jobs reference it
- name: Apply
  run: |
    deskribe apply \
      --image api=${{ needs.build.outputs.image-tag }}
```

For multi-service repos, pass multiple `--image` flags:

```bash
deskribe apply \
  --image api=ghcr.io/acme/payments-api:sha-abc123 \
  --image worker=ghcr.io/acme/payments-worker:sha-abc123
```

### Matrix Strategy for Multi-Environment Validation

The validate job uses a matrix to check all environments in parallel:

```yaml
strategy:
  matrix:
    environment: [dev, staging, prod]

steps:
  - name: Validate (${{ matrix.environment }})
    run: |
      deskribe validate \
        -f deskribe.json \
        --env ${{ matrix.environment }} \
        --platform ./platform-config
```

This catches environment-specific issues early. For example, a resource size that is valid in dev might violate a prod policy.

---

## 3. GitHub Actions -- Step by Step

This section breaks each job down individually with exact commands.

### Job 1: Build & Test

This job compiles the code, runs tests, and pushes a container image.

```yaml
build:
  runs-on: ubuntu-latest
  outputs:
    image-tag: ${{ steps.meta.outputs.tags }}

  steps:
    - uses: actions/checkout@v4

    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: "10.0.x"

    # Compile and run unit tests
    - run: dotnet restore
    - run: dotnet build --no-restore --configuration Release
    - run: dotnet test --no-build --configuration Release

    # Build and push container image
    - id: meta
      uses: docker/metadata-action@v5
      with:
        images: ghcr.io/${{ github.repository }}
        tags: type=sha,prefix=sha-

    - uses: docker/login-action@v3
      with:
        registry: ghcr.io
        username: ${{ github.actor }}
        password: ${{ secrets.GITHUB_TOKEN }}

    - uses: docker/build-push-action@v6
      with:
        context: .
        push: true
        tags: ${{ steps.meta.outputs.tags }}
```

**What happens:** Code is compiled, all tests pass, and a Docker image tagged with the commit SHA is pushed to GitHub Container Registry. The tag is passed to downstream jobs.

### Job 2: Validate

This job checks the manifest against platform policies. It runs after build because there is no point validating if the code does not compile.

```yaml
validate:
  needs: build
  runs-on: ubuntu-latest

  steps:
    - uses: actions/checkout@v4

    - name: Checkout platform config
      uses: actions/checkout@v4
      with:
        repository: acme/platform-config
        path: ./platform-config
        token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: "10.0.x"

    - run: dotnet tool install --global deskribe-cli

    # Validate against all environments
    - name: Validate (dev)
      run: |
        deskribe validate \
          -f deskribe.json \
          --env dev \
          --platform ./platform-config

    - name: Validate (staging)
      run: |
        deskribe validate \
          -f deskribe.json \
          --env staging \
          --platform ./platform-config

    - name: Validate (prod)
      run: |
        deskribe validate \
          -f deskribe.json \
          --env prod \
          --platform ./platform-config
```

**What happens:** Deskribe loads `deskribe.json`, merges it with the platform config for each environment, and checks all policies. If any validation fails, the step exits with code 1 and the pipeline stops.

### Job 3: Plan

This job generates a human-readable execution plan and posts it as a comment on the pull request.

```yaml
plan:
  needs: [build, validate]
  runs-on: ubuntu-latest

  steps:
    - uses: actions/checkout@v4

    - name: Checkout platform config
      uses: actions/checkout@v4
      with:
        repository: acme/platform-config
        path: ./platform-config
        token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: "10.0.x"

    - run: dotnet tool install --global deskribe-cli

    # Generate the plan with the image from the build job
    - name: Generate plan
      id: plan
      run: |
        deskribe plan \
          -f deskribe.json \
          --env dev \
          --platform ./platform-config \
          --image api=${{ needs.build.outputs.image-tag }} \
          | tee plan-output.txt

    # Post plan as PR comment so reviewers see the infra diff
    - name: Comment plan on PR
      if: github.event_name == 'pull_request'
      uses: actions/github-script@v7
      with:
        script: |
          const fs = require('fs');
          const plan = fs.readFileSync('plan-output.txt', 'utf8');
          github.rest.issues.createComment({
            issue_number: context.issue.number,
            owner: context.repo.owner,
            repo: context.repo.repo,
            body: `## Deskribe Plan\n\n\`\`\`\n${plan}\n\`\`\``
          });
```

**What happens:** Deskribe computes the merged config, resolves all `@resource()` references, and prints a plan showing what resources will be created/modified and what the workload will look like. This output is posted as a comment on the PR for reviewers to inspect.

### Job 4: Apply

This job provisions infrastructure and deploys the workload. It includes a manual approval gate for production.

```yaml
apply:
  needs: [build, validate, plan]
  if: github.event.inputs.action == 'apply'
  runs-on: ubuntu-latest
  # GitHub environment with protection rules enforces the approval gate
  environment: ${{ github.event.inputs.environment }}

  steps:
    - uses: actions/checkout@v4

    - name: Checkout platform config
      uses: actions/checkout@v4
      with:
        repository: acme/platform-config
        path: ./platform-config
        token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: "10.0.x"

    - run: dotnet tool install --global deskribe-cli

    # Authenticate to cloud provider
    - uses: azure/login@v2
      with:
        creds: ${{ secrets.AZURE_CREDENTIALS }}

    # Set Kubernetes context
    - uses: azure/aks-set-context@v4
      with:
        resource-group: ${{ secrets.AKS_RESOURCE_GROUP }}
        cluster-name: ${{ secrets.AKS_CLUSTER_NAME }}

    # Apply infrastructure + deploy workload
    - name: Apply
      run: |
        deskribe apply \
          -f deskribe.json \
          --env ${{ github.event.inputs.environment }} \
          --platform ./platform-config \
          --image api=${{ needs.build.outputs.image-tag }}
```

**What happens:** After a reviewer approves in the GitHub environment protection rule, Deskribe provisions all declared resources (Postgres, Redis, Kafka, etc.) via the configured backend (Pulumi/Terraform), then deploys the workload to Kubernetes with the correct connection strings injected.

---

## 4. Environment Promotion

Deskribe supports promoting deployments through environments with increasing levels of protection.

### Promotion Flow

```
  dev                    staging                  prod
  +---+                  +-------+                +------+
  |   | -- merge to  --> |       | -- tag v1.0 -> |      |
  |   |    main          |       |    + approval  |      |
  +---+                  +-------+                +------+
  Auto-deploy            Auto-deploy              Manual approval
  on push                on push to main          required
  No approval            Optional approval        2 reviewers
```

### GitHub Environments with Protection Rules

Configure three environments in your GitHub repository settings:

**dev** -- no protection rules:
```
Settings > Environments > dev
  - No required reviewers
  - No wait timer
  - Deployment branches: main, feature/*
```

**staging** -- optional protection:
```
Settings > Environments > staging
  - Required reviewers: 0 (or 1 for extra safety)
  - Wait timer: 0 minutes
  - Deployment branches: main only
```

**prod** -- strict protection:
```
Settings > Environments > prod
  - Required reviewers: 2 (team leads / platform team)
  - Wait timer: 5 minutes (cool-down period)
  - Deployment branches: main only
  - Custom deployment protection rule (optional): check monitoring
```

### Workflow for Promotion

```yaml
# Promote dev -> staging -> prod using workflow_dispatch
name: Promote

on:
  workflow_dispatch:
    inputs:
      from:
        description: "Source environment"
        required: true
        type: choice
        options: [dev, staging]
      to:
        description: "Target environment"
        required: true
        type: choice
        options: [staging, prod]
      image-tag:
        description: "Image tag to promote (e.g., sha-abc123)"
        required: true

jobs:
  promote:
    runs-on: ubuntu-latest
    # This triggers the environment protection rules
    environment: ${{ github.event.inputs.to }}

    steps:
      - uses: actions/checkout@v4

      - name: Checkout platform config
        uses: actions/checkout@v4
        with:
          repository: acme/platform-config
          path: ./platform-config
          token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - run: dotnet tool install --global deskribe-cli

      - name: Validate for target environment
        run: |
          deskribe validate \
            -f deskribe.json \
            --env ${{ github.event.inputs.to }} \
            --platform ./platform-config

      - name: Plan
        run: |
          deskribe plan \
            -f deskribe.json \
            --env ${{ github.event.inputs.to }} \
            --platform ./platform-config \
            --image api=ghcr.io/${{ github.repository }}:${{ github.event.inputs.image-tag }}

      - name: Apply
        run: |
          deskribe apply \
            -f deskribe.json \
            --env ${{ github.event.inputs.to }} \
            --platform ./platform-config \
            --image api=ghcr.io/${{ github.repository }}:${{ github.event.inputs.image-tag }}
```

### Manual Approval Before Production

When the `promote` job targets the `prod` environment, GitHub pauses the workflow and sends a review request to the required reviewers. The apply step only runs after the reviewers approve:

```
  Pipeline reaches "promote" job with environment: prod
       |
       v
  GitHub pauses the run
       |
       v
  Notification sent to required reviewers (2 people)
       |
       v
  Reviewers see the plan output in the workflow summary
       |
       v
  Both reviewers click "Approve"
       |
       v
  5-minute wait timer elapses (cool-down)
       |
       v
  Apply runs
```

---

## 5. Secrets Management in CI

### Platform Config Access

The platform config repository is typically private. Use a Personal Access Token (PAT) or a GitHub App token to clone it in CI:

```yaml
- name: Checkout platform config
  uses: actions/checkout@v4
  with:
    repository: acme/platform-config
    path: ./platform-config
    token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}
```

Store `PLATFORM_CONFIG_TOKEN` as a repository secret or (better) an organization secret shared across all repos that use Deskribe.

### Infrastructure Backend Credentials

Deskribe delegates infrastructure provisioning to backends like Pulumi or Terraform. These backends need cloud credentials:

**For Pulumi:**

```yaml
env:
  PULUMI_ACCESS_TOKEN: ${{ secrets.PULUMI_ACCESS_TOKEN }}
  PULUMI_BACKEND_URL: ${{ secrets.PULUMI_BACKEND_URL }}
```

**For Terraform:**

```yaml
env:
  TF_TOKEN_app_terraform_io: ${{ secrets.TF_API_TOKEN }}
  # Or for local state with Azure backend:
  ARM_ACCESS_KEY: ${{ secrets.ARM_ACCESS_KEY }}
```

### Cloud Provider Authentication

Deskribe needs credentials for the cloud provider where resources are provisioned, and for the Kubernetes cluster where workloads are deployed.

**Azure (recommended: federated identity):**

```yaml
permissions:
  id-token: write
  contents: read

steps:
  - uses: azure/login@v2
    with:
      client-id: ${{ secrets.AZURE_CLIENT_ID }}
      tenant-id: ${{ secrets.AZURE_TENANT_ID }}
      subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
```

**AWS:**

```yaml
steps:
  - uses: aws-actions/configure-aws-credentials@v4
    with:
      role-to-assume: ${{ secrets.AWS_ROLE_ARN }}
      aws-region: eu-west-1
```

**GCP:**

```yaml
steps:
  - uses: google-github-actions/auth@v2
    with:
      workload_identity_provider: ${{ secrets.GCP_WORKLOAD_IDENTITY_PROVIDER }}
      service_account: ${{ secrets.GCP_SERVICE_ACCOUNT }}
```

### Summary of Required Secrets

| Secret                     | Purpose                                   | Scope         |
|----------------------------|-------------------------------------------|---------------|
| `PLATFORM_CONFIG_TOKEN`    | Clone the platform config repo            | Organization  |
| `PULUMI_ACCESS_TOKEN`      | Authenticate to Pulumi Cloud              | Organization  |
| `AZURE_CREDENTIALS`        | Azure service principal (or federated ID) | Environment   |
| `AKS_RESOURCE_GROUP`       | Kubernetes cluster resource group         | Environment   |
| `AKS_CLUSTER_NAME`         | Kubernetes cluster name                   | Environment   |
| `AWS_ROLE_ARN`             | AWS IAM role for OIDC federation          | Environment   |

**Tip:** Use environment-scoped secrets for cloud credentials so that dev, staging, and prod each point to their own cloud accounts or subscriptions.

---

## 6. GitLab CI

Below is the equivalent pipeline for GitLab CI/CD.

```yaml
# .gitlab-ci.yml

stages:
  - build
  - validate
  - plan
  - apply

variables:
  DOTNET_VERSION: "10.0"
  PLATFORM_CONFIG_PATH: "./platform-config"

# ---------------------------------------------------
# Build Stage
# ---------------------------------------------------
build:
  stage: build
  image: mcr.microsoft.com/dotnet/sdk:10.0
  services:
    - docker:dind
  variables:
    DOCKER_HOST: tcp://docker:2376
  script:
    - dotnet restore
    - dotnet build --no-restore --configuration Release
    - dotnet test --no-build --configuration Release
    - |
      docker build -t $CI_REGISTRY_IMAGE:$CI_COMMIT_SHORT_SHA .
      docker push $CI_REGISTRY_IMAGE:$CI_COMMIT_SHORT_SHA
  artifacts:
    reports:
      dotenv: build.env

# ---------------------------------------------------
# Validate Stage (runs for all environments in parallel)
# ---------------------------------------------------
.validate-template: &validate-template
  stage: validate
  image: mcr.microsoft.com/dotnet/sdk:10.0
  needs: [build]
  before_script:
    - dotnet tool install --global deskribe-cli
    - export PATH="$PATH:$HOME/.dotnet/tools"
    - git clone https://oauth2:${PLATFORM_CONFIG_TOKEN}@gitlab.com/acme/platform-config.git $PLATFORM_CONFIG_PATH
  script:
    - deskribe validate -f deskribe.json --env $TARGET_ENV --platform $PLATFORM_CONFIG_PATH

validate-dev:
  <<: *validate-template
  variables:
    TARGET_ENV: dev

validate-staging:
  <<: *validate-template
  variables:
    TARGET_ENV: staging

validate-prod:
  <<: *validate-template
  variables:
    TARGET_ENV: prod

# ---------------------------------------------------
# Plan Stage
# ---------------------------------------------------
plan:
  stage: plan
  image: mcr.microsoft.com/dotnet/sdk:10.0
  needs: [build, validate-dev, validate-staging, validate-prod]
  before_script:
    - dotnet tool install --global deskribe-cli
    - export PATH="$PATH:$HOME/.dotnet/tools"
    - git clone https://oauth2:${PLATFORM_CONFIG_TOKEN}@gitlab.com/acme/platform-config.git $PLATFORM_CONFIG_PATH
  script:
    - |
      deskribe plan \
        -f deskribe.json \
        --env ${TARGET_ENV:-dev} \
        --platform $PLATFORM_CONFIG_PATH \
        --image api=$CI_REGISTRY_IMAGE:$CI_COMMIT_SHORT_SHA \
        | tee plan-output.txt
  artifacts:
    paths:
      - plan-output.txt
    expire_in: 30 days

# ---------------------------------------------------
# Apply Stage (manual trigger, environment-scoped)
# ---------------------------------------------------
.apply-template: &apply-template
  stage: apply
  image: mcr.microsoft.com/dotnet/sdk:10.0
  needs: [build, plan]
  when: manual
  before_script:
    - dotnet tool install --global deskribe-cli
    - export PATH="$PATH:$HOME/.dotnet/tools"
    - git clone https://oauth2:${PLATFORM_CONFIG_TOKEN}@gitlab.com/acme/platform-config.git $PLATFORM_CONFIG_PATH
  script:
    - |
      deskribe apply \
        -f deskribe.json \
        --env $TARGET_ENV \
        --platform $PLATFORM_CONFIG_PATH \
        --image api=$CI_REGISTRY_IMAGE:$CI_COMMIT_SHORT_SHA

apply-dev:
  <<: *apply-template
  variables:
    TARGET_ENV: dev
  environment:
    name: dev

apply-staging:
  <<: *apply-template
  variables:
    TARGET_ENV: staging
  environment:
    name: staging

apply-prod:
  <<: *apply-template
  variables:
    TARGET_ENV: prod
  environment:
    name: production
    # GitLab protected environments enforce approval
```

**GitLab-specific notes:**
- Use GitLab environments with protected environments for approval gates.
- `when: manual` prevents accidental applies.
- Store `PLATFORM_CONFIG_TOKEN`, `PULUMI_ACCESS_TOKEN`, and cloud credentials as CI/CD variables (masked and protected).
- Use YAML anchors (`&validate-template` / `<<: *validate-template`) to keep the file DRY.

---

## 7. Azure DevOps

Below is the equivalent pipeline for Azure DevOps.

```yaml
# azure-pipelines.yml

trigger:
  branches:
    include:
      - main
  tags:
    include:
      - "v*"

pr:
  branches:
    include:
      - main

pool:
  vmImage: "ubuntu-latest"

variables:
  dotnetVersion: "10.0.x"
  platformConfigRepo: "acme/platform-config"
  imageName: "$(containerRegistry)/payments-api"

stages:
  # ==========================================================
  # Stage 1: Build & Test
  # ==========================================================
  - stage: Build
    displayName: "Build & Test"
    jobs:
      - job: BuildAndTest
        steps:
          - task: UseDotNet@2
            inputs:
              version: $(dotnetVersion)

          - script: dotnet restore
            displayName: "Restore"

          - script: dotnet build --no-restore --configuration Release
            displayName: "Build"

          - script: dotnet test --no-build --configuration Release
            displayName: "Test"

          - task: Docker@2
            displayName: "Build & Push Image"
            inputs:
              containerRegistry: "$(dockerServiceConnection)"
              repository: "$(imageName)"
              command: "buildAndPush"
              Dockerfile: "**/Dockerfile"
              tags: |
                $(Build.BuildId)
                $(Build.SourceVersion)

  # ==========================================================
  # Stage 2: Validate
  # ==========================================================
  - stage: Validate
    displayName: "Validate Manifest"
    dependsOn: Build
    jobs:
      - job: ValidateAllEnvs
        strategy:
          matrix:
            dev:
              targetEnv: "dev"
            staging:
              targetEnv: "staging"
            prod:
              targetEnv: "prod"
        steps:
          - checkout: self

          - checkout: git://$(platformConfigRepo)@main
            path: platform-config

          - task: UseDotNet@2
            inputs:
              version: $(dotnetVersion)

          - script: dotnet tool install --global deskribe-cli
            displayName: "Install Deskribe CLI"

          - script: |
              deskribe validate \
                -f deskribe.json \
                --env $(targetEnv) \
                --platform $(Pipeline.Workspace)/platform-config
            displayName: "Validate ($(targetEnv))"

  # ==========================================================
  # Stage 3: Plan
  # ==========================================================
  - stage: Plan
    displayName: "Generate Plan"
    dependsOn: [Build, Validate]
    jobs:
      - job: GeneratePlan
        steps:
          - checkout: self

          - checkout: git://$(platformConfigRepo)@main
            path: platform-config

          - task: UseDotNet@2
            inputs:
              version: $(dotnetVersion)

          - script: dotnet tool install --global deskribe-cli
            displayName: "Install Deskribe CLI"

          - script: |
              deskribe plan \
                -f deskribe.json \
                --env dev \
                --platform $(Pipeline.Workspace)/platform-config \
                --image api=$(imageName):$(Build.SourceVersion)
            displayName: "Plan (dev)"

  # ==========================================================
  # Stage 4: Apply to Dev (auto)
  # ==========================================================
  - stage: ApplyDev
    displayName: "Apply to Dev"
    dependsOn: Plan
    condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))
    jobs:
      - deployment: DeployDev
        environment: "dev"
        strategy:
          runOnce:
            deploy:
              steps:
                - checkout: self

                - checkout: git://$(platformConfigRepo)@main
                  path: platform-config

                - task: UseDotNet@2
                  inputs:
                    version: $(dotnetVersion)

                - script: dotnet tool install --global deskribe-cli
                  displayName: "Install Deskribe CLI"

                - task: AzureCLI@2
                  inputs:
                    azureSubscription: "$(azureServiceConnection)"
                    scriptType: "bash"
                    scriptLocation: "inlineScript"
                    inlineScript: |
                      deskribe apply \
                        -f deskribe.json \
                        --env dev \
                        --platform $(Pipeline.Workspace)/platform-config \
                        --image api=$(imageName):$(Build.SourceVersion)
                  displayName: "Apply (dev)"

  # ==========================================================
  # Stage 5: Apply to Prod (manual approval via environment)
  # ==========================================================
  - stage: ApplyProd
    displayName: "Apply to Prod"
    dependsOn: ApplyDev
    condition: and(succeeded(), startsWith(variables['Build.SourceBranch'], 'refs/tags/v'))
    jobs:
      - deployment: DeployProd
        # Azure DevOps environment with approval gates
        environment: "production"
        strategy:
          runOnce:
            deploy:
              steps:
                - checkout: self

                - checkout: git://$(platformConfigRepo)@main
                  path: platform-config

                - task: UseDotNet@2
                  inputs:
                    version: $(dotnetVersion)

                - script: dotnet tool install --global deskribe-cli
                  displayName: "Install Deskribe CLI"

                - task: AzureCLI@2
                  inputs:
                    azureSubscription: "$(azureServiceConnectionProd)"
                    scriptType: "bash"
                    scriptLocation: "inlineScript"
                    inlineScript: |
                      deskribe apply \
                        -f deskribe.json \
                        --env prod \
                        --platform $(Pipeline.Workspace)/platform-config \
                        --image api=$(imageName):$(Build.SourceVersion)
                  displayName: "Apply (prod)"
```

**Azure DevOps-specific notes:**
- Use Azure DevOps environments with approval gates for the `production` environment.
- The `deployment` job type integrates with environment protection rules.
- Store secrets in Azure DevOps variable groups (scoped per environment) or Azure Key Vault.
- Use service connections for Azure and Docker registry authentication.
- The `AzureCLI@2` task handles Azure authentication before the Deskribe apply command runs.

---

## 8. Multi-Region Deployment

For organizations deploying to multiple regions (e.g., Azure AKS in US and EU), use a matrix
strategy with region-specific environment files.

### GitHub Actions -- Multi-Region Matrix

```yaml
# .github/workflows/deploy-multi-region.yml
name: Deploy Multi-Region

on:
  push:
    tags: ["v*"]

jobs:
  build:
    runs-on: ubuntu-latest
    outputs:
      image-tag: ${{ steps.meta.outputs.tags }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - run: dotnet restore && dotnet build --configuration Release && dotnet test --configuration Release
      - id: meta
        uses: docker/metadata-action@v5
        with:
          images: ghcr.io/${{ github.repository }}
          tags: type=sha,prefix=sha-
      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - uses: docker/build-push-action@v6
        with:
          context: .
          push: true
          tags: ${{ steps.meta.outputs.tags }}

  deploy:
    needs: build
    runs-on: ubuntu-latest
    strategy:
      matrix:
        env: [prod-us, prod-eu]
      fail-fast: false
    environment: ${{ matrix.env }}

    steps:
      - uses: actions/checkout@v4

      - name: Checkout platform config
        uses: actions/checkout@v4
        with:
          repository: acme/platform-config
          path: ./platform-config
          token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - run: dotnet tool install --global deskribe-cli

      - name: Validate (${{ matrix.env }})
        run: |
          deskribe validate \
            -f deskribe.json \
            --env ${{ matrix.env }} \
            --platform ./platform-config

      - name: Configure AKS context (${{ matrix.env }})
        uses: azure/aks-set-context@v4
        with:
          resource-group: ${{ secrets.AKS_RESOURCE_GROUP }}
          cluster-name: ${{ secrets.AKS_CLUSTER_NAME }}

      - name: Apply (${{ matrix.env }})
        run: |
          deskribe apply \
            -f deskribe.json \
            --env ${{ matrix.env }} \
            --platform ./platform-config \
            --image api=${{ needs.build.outputs.image-tag }}
```

### How It Works

The matrix strategy runs the deploy job twice in parallel:

```
  matrix.env = "prod-us"                         matrix.env = "prod-eu"
       |                                               |
       v                                               v
  Loads envs/prod-us.json                        Loads envs/prod-eu.json
    region: eastus2                                region: westeurope
       |                                               |
       v                                               v
  AKS context: aks-prod-us                       AKS context: aks-prod-eu
  (from environment secrets)                     (from environment secrets)
       |                                               |
       v                                               v
  deskribe apply --env prod-us                   deskribe apply --env prod-eu
  Provisions in eastus2                          Provisions in westeurope
  Deploys to aks-prod-us                         Deploys to aks-prod-eu
```

Each GitHub environment (`prod-us`, `prod-eu`) has its own secrets:

| Secret              | prod-us                     | prod-eu                     |
|---------------------|-----------------------------|-----------------------------|
| `AKS_RESOURCE_GROUP`| `rg-prod-us`               | `rg-prod-eu`               |
| `AKS_CLUSTER_NAME`  | `aks-prod-us`              | `aks-prod-eu`              |
| `AZURE_CREDENTIALS` | US subscription credentials | EU subscription credentials |

The developer's `deskribe.json` is identical for both regions. The `--env` flag selects which
region-specific environment file to use, and environment-scoped secrets point to the right cluster.
