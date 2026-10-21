import type { ModelOption, ModelRef } from '../../api/types'

export function modelToKey(model: ModelRef | null | undefined): string {
  if (!model) {
    return ''
  }
  return `${encodeURIComponent(model.providerId)}|${encodeURIComponent(model.modelId)}`
}

export function keyToModel(key: string): ModelRef | null {
  if (!key) {
    return null
  }
  const separator = key.indexOf('|')
  if (separator < 0) {
    return null
  }
  try {
    return {
      providerId: decodeURIComponent(key.slice(0, separator)),
      modelId: decodeURIComponent(key.slice(separator + 1)),
    }
  } catch {
    return null
  }
}

export function findModel(
  models: readonly ModelOption[],
  key: string,
): ModelOption | null {
  return models.find((model) => modelToKey(model) === key) ?? null
}
