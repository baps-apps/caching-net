.PHONY: sample-redis-up sample-redis-down sample-build sample-run sample-redis-validate

SAMPLE_PROJECT := Caching.NET.Sample.csproj
SAMPLE_COMPOSE := docker-compose.yml
SAMPLE_URL ?= http://localhost:5151

sample-redis-up:
	docker compose -f "$(SAMPLE_COMPOSE)" up -d

sample-redis-down:
	docker compose -f "$(SAMPLE_COMPOSE)" down

sample-build:
	dotnet build "$(SAMPLE_PROJECT)"

sample-run:
	dotnet run --project "$(SAMPLE_PROJECT)" --launch-profile http

sample-redis-validate:
	curl -fsS -X POST "$(SAMPLE_URL)/catalog/redis/validate"
